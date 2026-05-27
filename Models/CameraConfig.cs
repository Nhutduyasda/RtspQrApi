using System.ComponentModel.DataAnnotations;

namespace RtspQrApi.Models;

public sealed class CameraConfig
{
    [Required]
    public required string Id { get; init; }

    [Required]
    public required string Name { get; init; }

    [Required]
    public required string RtspUrl { get; init; }
}
