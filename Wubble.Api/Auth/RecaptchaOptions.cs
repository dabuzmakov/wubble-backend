namespace Wubble.Api.Auth;

public sealed class RecaptchaOptions
{
    public const string Section = "Recaptcha";

    public string SecretKey { get; init; } = "";
}