namespace RtspQrApi.Models;

public sealed record QrScanResult(
    long Id,
    string CameraId,
    string Value,
    DateTimeOffset DetectedAt,
    DateTimeOffset FrameAt);
