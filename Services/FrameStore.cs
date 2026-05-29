namespace RtspQrApi.Services;

public sealed class FrameStore
{
    private readonly object _sync = new();
    private byte[]? _latestJpeg;
    private DateTimeOffset? _lastFrameAt;
    private long _version;
    private double _fps;
    private int _width;
    private int _height;
    private TaskCompletionSource _frameSignal = CreateFrameSignal();

    public void Update(byte[] jpeg, int width, int height, double fps, DateTimeOffset receivedAt)
    {
        TaskCompletionSource frameSignal;

        lock (_sync)
        {
            _latestJpeg = jpeg;
            _width = width;
            _height = height;
            _fps = fps;
            _lastFrameAt = receivedAt;
            _version++;
            frameSignal = _frameSignal;
            _frameSignal = CreateFrameSignal();
        }

        frameSignal.TrySetResult();
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

    public bool TryGetSnapshot(out FrameSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_latestJpeg is null)
            {
                snapshot = FrameSnapshot.Empty;
                return false;
            }

            snapshot = new FrameSnapshot(_latestJpeg, _lastFrameAt, _version);
            return true;
        }
    }

    public async ValueTask<FrameSnapshot?> WaitForSnapshotAsync(long afterVersion, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Task waitTask;
            lock (_sync)
            {
                if (_latestJpeg is not null && _version > afterVersion)
                {
                    return new FrameSnapshot(_latestJpeg, _lastFrameAt, _version);
                }

                waitTask = _frameSignal.Task;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public FrameInfo GetInfo()
    {
        lock (_sync)
        {
            return new FrameInfo(_width, _height, _fps, _lastFrameAt);
        }
    }

    private static TaskCompletionSource CreateFrameSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed record FrameInfo(int Width, int Height, double Fps, DateTimeOffset? LastFrameAt);

public sealed record FrameSnapshot(byte[] Jpeg, DateTimeOffset? LastFrameAt, long Version)
{
    public static FrameSnapshot Empty { get; } = new([], null, 0);
}
