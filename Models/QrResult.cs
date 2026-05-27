namespace RtspQrApi.Models;

public sealed record QrResult(
    string CameraId,
    bool Found,
    string? Value,
    DateTimeOffset? DetectedAt);
