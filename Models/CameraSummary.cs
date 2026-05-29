namespace RtspQrApi.Models;

public sealed record CameraSummary(
    string Id,
    string Name,
    string RtspUrl,
    CameraStatus Status,
    QrResult LatestQr);
