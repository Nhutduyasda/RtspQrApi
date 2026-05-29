using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OpenCvSharp;
using RtspQrApi.Data;
using RtspQrApi.Models;

namespace RtspQrApi.Services;

public sealed class QrProcessor
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(500);
    private static readonly StringComparer ValueComparer = StringComparer.Ordinal;

    private readonly ConcurrentDictionary<string, CameraQrState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDbContextFactory<RtspQrDbContext> _dbContextFactory;
    private readonly ILogger<QrProcessor> _logger;

    public QrProcessor(IDbContextFactory<RtspQrDbContext> dbContextFactory, ILogger<QrProcessor> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public QrResult GetLatest(string cameraId)
    {
        return _states.TryGetValue(cameraId, out var state)
            ? state.GetLatest()
            : new QrResult(cameraId, Found: false, Value: null, DetectedAt: null);
    }

    public void ForgetCamera(string cameraId)
    {
        _states.TryRemove(cameraId, out _);
    }

    public async Task<IReadOnlyList<QrScanResult>> GetHistoryAsync(string cameraId, int take, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(take, 1, 200);

        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await dbContext.QrScanRecords
                .AsNoTracking()
                .Where(record => record.CameraId == cameraId)
                .OrderByDescending(record => record.DetectedAt)
                .ThenByDescending(record => record.Id)
                .Take(limit)
                .Select(record => new QrScanResult(
                    record.Id,
                    record.CameraId,
                    record.Value,
                    record.DetectedAt,
                    record.FrameAt))
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not load QR history for camera {CameraId}", cameraId);
            return [];
        }
    }

    public async Task ProcessFrameAsync(string cameraId, Mat frame, DateTimeOffset frameAt, CancellationToken cancellationToken)
    {
        var state = _states.GetOrAdd(cameraId, static id => new CameraQrState(id));
        if (!state.TryBeginScan(frameAt, ScanInterval))
        {
            return;
        }

        try
        {
            using var detector = new QRCodeDetector();
            var values = DecodeQrValues(detector, frame);
            if (values.Count == 0)
            {
                state.UpdateLatest(new QrResult(cameraId, Found: false, Value: null, DetectedAt: null));
                return;
            }

            var detectedAt = DateTimeOffset.UtcNow;
            state.UpdateLatest(new QrResult(cameraId, Found: true, Value: values[0], DetectedAt: detectedAt));

            await SaveDetectedValuesAsync(cameraId, values, detectedAt, frameAt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not process QR frame for camera {CameraId}", cameraId);
        }
    }

    private static IReadOnlyList<string> DecodeQrValues(QRCodeDetector detector, Mat frame)
    {
        var values = new List<string>();

        if (detector.DetectMulti(frame, out var points) &&
            points.Length > 0 &&
            detector.DecodeMulti(frame, points, out var decodedInfo))
        {
            values.AddRange(decodedInfo.Where(value => value is not null)!);
        }

        if (values.Count == 0)
        {
            using var straightQrCode = new Mat();
            values.Add(detector.DetectAndDecode(frame, out _, straightQrCode));
        }

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(ValueComparer)
            .ToArray();
    }

    private async Task SaveDetectedValuesAsync(
        string cameraId,
        IReadOnlyCollection<string> values,
        DateTimeOffset detectedAt,
        DateTimeOffset frameAt,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existingRecords = await dbContext.QrScanRecords
            .Where(record => record.CameraId == cameraId && values.Contains(record.Value))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var existingValueSet = existingRecords
            .Select(record => record.Value)
            .ToHashSet(ValueComparer);

        foreach (var record in existingRecords)
        {
            record.DetectedAt = detectedAt;
            record.FrameAt = frameAt;
        }

        var newValues = values
            .Where(value => !existingValueSet.Contains(value))
            .ToArray();

        foreach (var value in newValues)
        {
            dbContext.QrScanRecords.Add(new QrScanRecord
            {
                CameraId = cameraId,
                Value = value,
                DetectedAt = detectedAt,
                FrameAt = frameAt
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("QR detected on camera {CameraId}: {QrValues}", cameraId, string.Join(", ", values));
    }

    private sealed class CameraQrState
    {
        private readonly object _sync = new();
        private DateTimeOffset _lastScanAt = DateTimeOffset.MinValue;
        private QrResult _latest;

        public CameraQrState(string cameraId)
        {
            _latest = new QrResult(cameraId, Found: false, Value: null, DetectedAt: null);
        }

        public bool TryBeginScan(DateTimeOffset frameAt, TimeSpan scanInterval)
        {
            lock (_sync)
            {
                if (frameAt - _lastScanAt < scanInterval)
                {
                    return false;
                }

                _lastScanAt = frameAt;
                return true;
            }
        }

        public QrResult GetLatest()
        {
            lock (_sync)
            {
                return _latest;
            }
        }

        public void UpdateLatest(QrResult result)
        {
            lock (_sync)
            {
                _latest = result;
            }
        }

    }
}
