namespace RtspQrApi.Services;

public sealed class FrameStore
{
    private readonly object _sync = new();
    private byte[]? _latestJpeg;
    private DateTimeOffset? _lastFrameAt;
    private double _fps;
    private int _width;
    private int _height;

    public void Update(byte[] jpeg, int width, int height, double fps, DateTimeOffset receivedAt)
    {
        lock (_sync)
        {
            _latestJpeg = jpeg;
            _width = width;
            _height = height;
            _fps = fps;
            _lastFrameAt = receivedAt;
        }
    }

    public bool TryGetSnapshot(out byte[] jpeg, out DateTimeOffset? lastFrameAt)
    {
        lock (_sync)
        {
            lastFrameAt = _lastFrameAt;

            if (_latestJpeg is null)
            {
                jpeg = [];
                return false;
            }

            jpeg = _latestJpeg;
            return true;
        }
    }

    public FrameInfo GetInfo()
    {
        lock (_sync)
        {
            return new FrameInfo(_width, _height, _fps, _lastFrameAt);
        }
    }
}

public sealed record FrameInfo(int Width, int Height, double Fps, DateTimeOffset? LastFrameAt);
