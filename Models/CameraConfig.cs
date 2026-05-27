using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace RtspQrApi.Models;

public sealed class CameraConfig : IValidatableObject
{
    [Required]
    public required string Id { get; init; }

    [Required]
    public required string Name { get; init; }

    [Required]
    public required string RtspUrl { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var id = Id?.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            yield return new ValidationResult("Camera id is required.", [nameof(Id)]);
        }
        else if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,64}$"))
        {
            yield return new ValidationResult("Camera id can contain only letters, numbers, hyphen, and underscore, up to 64 characters.", [nameof(Id)]);
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            yield return new ValidationResult("Camera name is required.", [nameof(Name)]);
        }

        var rtspUrl = RtspUrl?.Trim();
        if (string.IsNullOrWhiteSpace(rtspUrl))
        {
            yield return new ValidationResult("RTSP URL is required.", [nameof(RtspUrl)]);
        }
        else if (!Uri.TryCreate(rtspUrl, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != "rtsp" && uri.Scheme != "rtsps"))
        {
            yield return new ValidationResult("RTSP URL must be an absolute rtsp:// or rtsps:// URL.", [nameof(RtspUrl)]);
        }
    }
}
