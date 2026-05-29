using System.ComponentModel.DataAnnotations;

namespace RtspQrApi.Data;

public sealed class QrScanRecord
{
    public long Id { get; set; }

    [MaxLength(64)]
    public required string CameraId { get; set; }

    [MaxLength(2048)]
    public required string Value { get; set; }

    public DateTimeOffset DetectedAt { get; set; }

    public DateTimeOffset FrameAt { get; set; }
}
