using System.ComponentModel.DataAnnotations;

namespace RtspQrApi.Data;

public sealed class CameraConfigRecord
{
    [MaxLength(64)]
    public required string Id { get; set; }

    [MaxLength(200)]
    public required string Name { get; set; }

    [MaxLength(2048)]
    public required string RtspUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
