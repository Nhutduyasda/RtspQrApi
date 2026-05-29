using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RtspQrApi.Data;
using RtspQrApi.Models;

namespace RtspQrApi.Services;

public sealed class CameraManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CameraConfig> _configs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FrameStore> _frameStores = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CameraWorker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _workerGate = new(1, 1);
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDbContextFactory<RtspQrDbContext> _dbContextFactory;
    private readonly QrProcessor _qrProcessor;
    private readonly ILogger<CameraManager> _logger;

    public CameraManager(
        ILoggerFactory loggerFactory,
        IDbContextFactory<RtspQrDbContext> dbContextFactory,
        QrProcessor qrProcessor,
        ILogger<CameraManager> logger)
    {
        _loggerFactory = loggerFactory;
        _dbContextFactory = dbContextFactory;
        _qrProcessor = qrProcessor;
        _logger = logger;
    }

    public async Task LoadPersistedCamerasAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var records = await dbContext.CameraConfigs
                .AsNoTracking()
                .OrderBy(record => record.Id)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var record in records)
            {
                var config = new CameraConfig
                {
                    Id = record.Id,
                    Name = record.Name,
                    RtspUrl = record.RtspUrl
                };

                _configs[config.Id] = config;
                _frameStores.TryAdd(config.Id, new FrameStore());
            }

            _logger.LogInformation("Loaded {CameraCount} persisted camera configs", records.Length);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not load persisted camera configs. Cameras can still be added in memory after the database is available.");
        }
    }

    public async Task<bool> AddCameraAsync(CameraConfig config, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(config);

        await _workerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_configs.ContainsKey(normalized.Id))
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            if (await dbContext.CameraConfigs.AnyAsync(record => record.Id == normalized.Id, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            dbContext.CameraConfigs.Add(new CameraConfigRecord
            {
                Id = normalized.Id,
                Name = normalized.Name,
                RtspUrl = normalized.RtspUrl,
                CreatedAt = now,
                UpdatedAt = now
            });

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _configs[normalized.Id] = normalized;
            _frameStores.TryAdd(normalized.Id, new FrameStore());
            return true;
        }
        finally
        {
            _workerGate.Release();
        }
    }

    public async Task<bool> DeleteCameraAsync(string id, CancellationToken cancellationToken = default)
    {
        await _workerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_configs.ContainsKey(id))
            {
                return false;
            }

            if (!await StopWorkerLockedAsync(id).ConfigureAwait(false))
            {
                return false;
            }

            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var record = await dbContext.CameraConfigs.FindAsync([id], cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                dbContext.CameraConfigs.Remove(record);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            _configs.TryRemove(id, out _);
            _frameStores.TryRemove(id, out _);
            _qrProcessor.ForgetCamera(id);
            return true;
        }
        finally
        {
            _workerGate.Release();
        }
    }

    public IReadOnlyCollection<CameraConfig> GetCameras()
    {
        return _configs.Values
            .OrderBy(camera => camera.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool CameraExists(string id)
    {
        return _configs.ContainsKey(id);
    }

    public async Task<bool> StartAsync(string id)
    {
        await _workerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await StartWorkerLockedAsync(id).ConfigureAwait(false);
        }
        finally
        {
            _workerGate.Release();
        }
    }

    public async Task<IReadOnlyList<CameraStatus>> StartAllAsync()
    {
        await _workerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var id in _configs.Keys.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
            {
                _ = await StartWorkerLockedAsync(id).ConfigureAwait(false);
            }
        }
        finally
        {
            _workerGate.Release();
        }

        return GetAllStatuses();
    }

    public async Task<bool> StopAsync(string id)
    {
        await _workerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await StopWorkerLockedAsync(id).ConfigureAwait(false);
        }
        finally
        {
            _workerGate.Release();
        }
    }

    public async Task<IReadOnlyList<CameraStatus>> StopAllAsync()
    {
        await _workerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var id in _configs.Keys.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
            {
                _ = await StopWorkerLockedAsync(id).ConfigureAwait(false);
            }
        }
        finally
        {
            _workerGate.Release();
        }

        return GetAllStatuses();
    }

    public CameraStatus? GetStatus(string id)
    {
        if (!_configs.ContainsKey(id))
        {
            return null;
        }

        if (_workers.TryGetValue(id, out var worker))
        {
            return worker.GetStatus();
        }

        var frameInfo = _frameStores.GetOrAdd(id, _ => new FrameStore()).GetInfo();
        return new CameraStatus(id, false, false, frameInfo.Fps, frameInfo.Width, frameInfo.Height, frameInfo.LastFrameAt, 0, null);
    }

    public bool TryGetSnapshot(string id, out byte[] jpeg, out DateTimeOffset? lastFrameAt)
    {
        jpeg = [];
        lastFrameAt = null;

        return _frameStores.TryGetValue(id, out var store) && store.TryGetSnapshot(out jpeg, out lastFrameAt);
    }

    public ValueTask<FrameSnapshot?> WaitForSnapshotAsync(string id, long afterVersion, CancellationToken cancellationToken)
    {
        return _frameStores.TryGetValue(id, out var store)
            ? store.WaitForSnapshotAsync(afterVersion, cancellationToken)
            : ValueTask.FromResult<FrameSnapshot?>(null);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var worker in _workers.Values)
        {
            await worker.DisposeAsync().ConfigureAwait(false);
        }

        _workers.Clear();
        _workerGate.Dispose();
    }

    private async Task<bool> StartWorkerLockedAsync(string id)
    {
        if (!_configs.TryGetValue(id, out var config))
        {
            return false;
        }

        if (_workers.TryGetValue(id, out var existingWorker) &&
            existingWorker.IsStopRequested &&
            !existingWorker.IsRunning)
        {
            _workers.TryRemove(id, out _);
            await existingWorker.DisposeAsync().ConfigureAwait(false);
        }

        var worker = _workers.GetOrAdd(id, cameraId =>
        {
            var store = _frameStores.GetOrAdd(cameraId, _ => new FrameStore());
            var logger = _loggerFactory.CreateLogger<CameraWorker>();
            return new CameraWorker(config, store, _qrProcessor, logger);
        });

        await worker.StartAsync().ConfigureAwait(false);
        return true;
    }

    private async Task<bool> StopWorkerLockedAsync(string id)
    {
        if (!_configs.ContainsKey(id))
        {
            return false;
        }

        if (!_workers.TryGetValue(id, out var worker))
        {
            return true;
        }

        var stopped = await worker.StopAsync().ConfigureAwait(false);
        if (!stopped)
        {
            return false;
        }

        _workers.TryRemove(id, out _);
        await worker.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    private IReadOnlyList<CameraStatus> GetAllStatuses()
    {
        return GetCameras()
            .Select(camera => GetStatus(camera.Id))
            .Where(status => status is not null)
            .Select(status => status!)
            .ToArray();
    }

    private static CameraConfig Normalize(CameraConfig config)
    {
        return new CameraConfig
        {
            Id = config.Id.Trim(),
            Name = config.Name.Trim(),
            RtspUrl = config.RtspUrl.Trim()
        };
    }
}
