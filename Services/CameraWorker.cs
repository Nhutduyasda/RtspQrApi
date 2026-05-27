using OpenCvSharp;
using RtspQrApi.Models;

namespace RtspQrApi.Services;

public sealed class CameraWorker : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StaleFrameTimeout = TimeSpan.FromSeconds(5);

    private readonly CameraConfig _config;
    private readonly FrameStore _frameStore;
    private readonly ILogger<CameraWorker> _logger;
    private readonly object _statusSync = new();
    private readonly CancellationTokenSource _stoppingCts = new();
    private Task? _runTask;
    private bool _isConnected;
    private string? _lastError;
    private int _reconnectCount;

    public CameraWorker(CameraConfig config, FrameStore frameStore, ILogger<CameraWorker> logger)
    {
        _config = config;
        _frameStore = frameStore;
        _logger = logger;
    }

    public bool IsRunning => _runTask is { IsCompleted: false };

    public Task StartAsync()
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _runTask = Task.Run(() => RunAsync(_stoppingCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var runTask = _runTask;
        if (runTask is null || runTask.IsCompleted)
        {
            MarkDisconnected();
            return;
        }

        await _stoppingCts.CancelAsync();

        try
        {
            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during a normal stop.
        }
        finally
        {
            MarkDisconnected();
        }
    }

    public CameraStatus GetStatus()
    {
        var frameInfo = _frameStore.GetInfo();
        var connected = IsConnectionFresh(frameInfo.LastFrameAt);

        lock (_statusSync)
        {
            if (_isConnected && !connected)
            {
                _isConnected = false;
                _lastError ??= "No new frame within the last 5 seconds.";
            }

            return new CameraStatus(
                _config.Id,
                IsRunning,
                _isConnected && connected,
                Math.Round(frameInfo.Fps, 2),
                frameInfo.Width,
                frameInfo.Height,
                frameInfo.LastFrameAt,
                _reconnectCount,
                _lastError);
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReadStreamAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                MarkDisconnected(ex.Message);
                _logger.LogWarning(ex, "Camera {CameraId} disconnected", _config.Id);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            IncrementReconnectCount();
            _logger.LogInformation("Reconnecting {CameraId} in {Seconds} seconds", _config.Id, ReconnectDelay.TotalSeconds);
            await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ReadStreamAsync(CancellationToken stoppingToken)
    {
        using var capture = new VideoCapture(_config.RtspUrl);

        if (!capture.IsOpened())
        {
            throw new InvalidOperationException($"Could not open RTSP stream: {_config.RtspUrl}");
        }

        MarkConnected();

        var fps = capture.Fps;
        if (double.IsNaN(fps) || fps <= 0)
        {
            fps = 0;
        }

        using var frame = new Mat();
        var loggedFrameInfo = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var read = capture.Read(frame);
            if (!read || frame.Empty())
            {
                throw new InvalidOperationException("OpenCV could not read a frame from the stream.");
            }

            var receivedAt = DateTimeOffset.UtcNow;
            Cv2.ImEncode(".jpg", frame, out var buffer);
            _frameStore.Update(buffer.ToArray(), frame.Width, frame.Height, fps, receivedAt);

            if (!loggedFrameInfo)
            {
                _logger.LogInformation("Frame received from {CameraId}: {Width}x{Height}, FPS: {Fps}", _config.Id, frame.Width, frame.Height, fps);
                loggedFrameInfo = true;
            }

            if (DateTimeOffset.UtcNow - receivedAt > StaleFrameTimeout)
            {
                throw new InvalidOperationException("Frame reader stalled.");
            }

            await Task.Yield();
        }
    }

    private bool IsConnectionFresh(DateTimeOffset? lastFrameAt)
    {
        return lastFrameAt.HasValue && DateTimeOffset.UtcNow - lastFrameAt.Value <= StaleFrameTimeout;
    }

    private void MarkConnected()
    {
        lock (_statusSync)
        {
            _isConnected = true;
            _lastError = null;
        }

        _logger.LogInformation("Camera {CameraId} connected", _config.Id);
    }

    private void MarkDisconnected(string? error = null)
    {
        lock (_statusSync)
        {
            _isConnected = false;
            _lastError = error;
        }
    }

    private void IncrementReconnectCount()
    {
        lock (_statusSync)
        {
            _reconnectCount++;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stoppingCts.Dispose();
    }
}
