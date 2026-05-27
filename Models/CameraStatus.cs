namespace RtspQrApi.Models;

public sealed record CameraStatus(
    string CameraId,
    bool IsRunning,
    bool IsConnected,
    double Fps,
    int Width,
    int Height,
    DateTimeOffset? LastFrameAt,
    int ReconnectCount,
    string? LastError);
