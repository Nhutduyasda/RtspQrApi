using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace RtspQrApi.Auth;

public sealed class BasicAuthMiddleware
{
    private const string Scheme = "Basic";
    private readonly RequestDelegate _next;
    private readonly BasicAuthOptions _options;
    private readonly ILogger<BasicAuthMiddleware> _logger;

    public BasicAuthMiddleware(
        RequestDelegate next,
        IOptions<BasicAuthOptions> options,
        ILogger<BasicAuthMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (TryAuthenticate(context.Request.Headers.Authorization, out var userName))
        {
            context.Items["BasicAuthUser"] = userName;
            await _next(context).ConfigureAwait(false);
            return;
        }

        Challenge(context);
    }

    private bool TryAuthenticate(string? authorizationHeader, out string userName)
    {
        userName = string.Empty;

        if (!AuthenticationHeaderValue.TryParse(authorizationHeader, out var header) ||
            !string.Equals(header.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Invalid Basic Auth header.");
            return false;
        }

        var separatorIndex = decoded.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return false;
        }

        var candidateUserName = decoded[..separatorIndex];
        var candidatePassword = decoded[(separatorIndex + 1)..];
        if (!FixedTimeEquals(candidateUserName, _options.Username) ||
            !FixedTimeEquals(candidatePassword, _options.Password))
        {
            return false;
        }

        userName = candidateUserName;
        return true;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static void Challenge(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"RTSPQR Dashboard\", charset=\"UTF-8\"";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }
}
