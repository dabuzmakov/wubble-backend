using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Wubble.Api.Auth;

public sealed class RecaptchaVerifier(
    HttpClient httpClient,
    IOptions<RecaptchaOptions> options)
{
    private const string VerifyUrl =
        "https://www.google.com/recaptcha/api/siteverify";

    public async Task<bool> IsValidAsync(
        string token,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var parameters = new Dictionary<string, string>
        {
            ["secret"] = options.Value.SecretKey,
            ["response"] = token
        };

        using var content = new FormUrlEncodedContent(parameters);

        using var response = await httpClient.PostAsync(
            VerifyUrl,
            content,
            ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content
            .ReadFromJsonAsync<RecaptchaResponse>(ct);

        return result?.Success == true;
    }

    private sealed record RecaptchaResponse(
        [property: JsonPropertyName("success")] bool Success);
}