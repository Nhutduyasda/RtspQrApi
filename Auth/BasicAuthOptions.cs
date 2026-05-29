namespace RtspQrApi.Auth;

public sealed class BasicAuthOptions
{
    public bool Enabled { get; set; } = true;

    public string Username { get; set; } = "admin";

    public string Password { get; set; } = "admin123";
}
