using System.Collections.Concurrent;
using RtspQrApi.Models;

namespace RtspQrApi.Services;

public sealed class CameraManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CameraConfig> _configs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FrameStore> _frameStores = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CameraWorker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILoggerFactory _loggerFactory;

    public CameraManager(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public bool AddCamera(CameraConfig config)
    {
        var normalized = new CameraConfig
        {
            Id = config.Id.Trim(),
            Name = config.Name.Trim(),
            RtspUrl = config.RtspUrl.Trim()
        };

        if (!_configs.TryAdd(normalized.Id, normalized))
        {
            return false;
        }

        _frameStores.TryAdd(normalized.Id, new FrameStore());
        return true;
    }

    public bool CameraExists(string id)
    {
        return _configs.ContainsKey(id);
    }

    public Task<bool> StartAsync(string id)
    {
        if (!_configs.TryGetValue(id, out var config))
        {
            return Task.FromResult(false);
        }

        var worker = _workers.GetOrAdd(id, cameraId =>
        {
            var store = _frameStores.GetOrAdd(cameraId, _ => new FrameStore());
            var logger = _loggerFactory.CreateLogger<CameraWorker>();
            return new CameraWorker(config, store, logger);
        });

        return worker.StartAsync().ContinueWith(_ => true, TaskScheduler.Default);
    }

    public async Task<bool> StopAsync(string id)
    {
        if (!_workers.TryRemove(id, out var worker))
        {
            return _configs.ContainsKey(id);
        }

        await worker.DisposeAsync().ConfigureAwait(false);
        return true;
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

    public async ValueTask DisposeAsync()
    {
        foreach (var worker in _workers.Values)
        {
            await worker.DisposeAsync().ConfigureAwait(false);
        }

        _workers.Clear();
    }
}
