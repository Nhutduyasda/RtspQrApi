using System.Net.Sockets;
using OpenCvSharp;
using RtspQrApi.Models;

namespace RtspQrApi.Services;

public sealed class CameraWorker : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StaleFrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RtspProbeTimeout = TimeSpan.FromSeconds(1);
    private const int CaptureOpenTimeoutMilliseconds = 5000;
    private const int CaptureReadTimeoutMilliseconds = 5000;
    private const int OpenTimeoutPropertyId = 53;
    private const int ReadTimeoutPropertyId = 54;

    private readonly CameraConfig _config;
    private readonly FrameStore _frameStore;
    private readonly QrProcessor _qrProcessor;
    private readonly ILogger<CameraWorker> _logger;
    private readonly object _lifecycleSync = new();
    private readonly object _statusSync = new();
    private readonly CancellationTokenSource _stoppingCts = new();
    private Task? _runTask;
    private bool _isConnected;
    private string? _lastError;
    private int _reconnectCount;

    public CameraWorker(CameraConfig config, FrameStore frameStore, QrProcessor qrProcessor, ILogger<CameraWorker> logger)
    {
        _config = config;
        _frameStore = frameStore;
        _qrProcessor = qrProcessor;
        _logger = logger;
    }

    public bool IsRunning => _runTask is { IsCompleted: false };

    public bool IsStopRequested => _stoppingCts.IsCancellationRequested;

    public Task StartAsync()
    {
        lock (_lifecycleSync)
        {
            if (IsRunning)
            {
                return Task.CompletedTask;
            }

            _runTask = Task.Run(() => RunAsync(_stoppingCts.Token));
            return Task.CompletedTask;
        }
    }

    public async Task<bool> StopAsync()
    {
        Task? runTask;
        lock (_lifecycleSync)
        {
            runTask = _runTask;
        }

        if (runTask is null || runTask.IsCompleted)
        {
            MarkDisconnected();
            return true;
        }

        await _stoppingCts.CancelAsync();

        var completedTask = await Task.WhenAny(runTask, Task.Delay(StopTimeout)).ConfigureAwait(false);
        if (completedTask != runTask)
        {
            MarkDisconnected("Stop requested, but RTSP reader did not finish within the timeout.");
            _logger.LogWarning("Camera {CameraId} did not stop within {Seconds} seconds", _config.Id, StopTimeout.TotalSeconds);
            return false;
        }

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

        return true;
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
            catch (Exception) when (stoppingToken.IsCancellationRequested)
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
        using var capture = new VideoCapture();

        await EnsureRtspServerReachableAsync(_config.RtspUrl, stoppingToken).ConfigureAwait(false);
        ConfigureCapture(capture);

        if (!capture.Open(_config.RtspUrl, VideoCaptureAPIs.FFMPEG) && !capture.Open(_config.RtspUrl))
        {
            throw new InvalidOperationException($"Could not open RTSP stream: {_config.RtspUrl}");
        }

        var fps = capture.Fps;
        if (double.IsNaN(fps) || fps <= 0)
        {
            fps = 0;
        }

        using var frame = new Mat();
        var loggedFrameInfo = false;
        var lastFrameAt = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            var read = capture.Read(frame);
            if (!read || frame.Empty())
            {
                var elapsed = DateTimeOffset.UtcNow - lastFrameAt;
                throw new InvalidOperationException($"OpenCV could not read a frame for {elapsed.TotalSeconds:F1} seconds.");
            }

            var receivedAt = DateTimeOffset.UtcNow;
            Cv2.ImEncode(".jpg", frame, out var buffer);
            _frameStore.Update(buffer.ToArray(), frame.Width, frame.Height, fps, receivedAt);
            await _qrProcessor.ProcessFrameAsync(_config.Id, frame, receivedAt, stoppingToken).ConfigureAwait(false);
            lastFrameAt = receivedAt;

            if (!loggedFrameInfo)
            {
                MarkConnected();
                _logger.LogInformation("Frame received from {CameraId}: {Width}x{Height}, FPS: {Fps}", _config.Id, frame.Width, frame.Height, fps);
                loggedFrameInfo = true;
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

    private static void ConfigureCapture(VideoCapture capture)
    {
        capture.Set((VideoCaptureProperties)OpenTimeoutPropertyId, CaptureOpenTimeoutMilliseconds);
        capture.Set((VideoCaptureProperties)ReadTimeoutPropertyId, CaptureReadTimeoutMilliseconds);
    }

    private static async Task EnsureRtspServerReachableAsync(string rtspUrl, CancellationToken stoppingToken)
    {
        if (!Uri.TryCreate(rtspUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "rtsp" && uri.Scheme != "rtsps"))
        {
            return;
        }

        var port = uri.Port > 0
            ? uri.Port
            : string.Equals(uri.Scheme, "rtsps", StringComparison.OrdinalIgnoreCase) ? 322 : 554;

        using var client = new TcpClient();
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        probeCts.CancelAfter(RtspProbeTimeout);

        try
        {
            await client.ConnectAsync(uri.Host, port, probeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"RTSP server {uri.Host}:{port} did not respond within {RtspProbeTimeout.TotalSeconds:F0} second.");
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"RTSP server {uri.Host}:{port} is not reachable: {ex.SocketErrorCode}.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _ = await StopAsync().ConfigureAwait(false);
        _stoppingCts.Dispose();
    }
}
