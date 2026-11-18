using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Wubble.Api.Email;

/// <summary>
/// Отправка транзакционных писем через Brevo API.
/// </summary>
public sealed class BrevoMailSender(
    HttpClient httpClient,
    IOptions<EmailOptions> options) : IMailSender
{
  private const string ApiUrl = "https://api.brevo.com/v3/smtp/email";

  private readonly EmailOptions _options = options.Value;

  public async Task SendAsync(OutgoingEmail email, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(_options.ApiKey))
      throw new InvalidOperationException("Brevo API key is not configured.");

    var requestBody = new
    {
      sender = new
      {
        name = _options.FromName,
        email = _options.FromAddress
      },
      to = new[]
      {
                new { email = email.To }
      },
      subject = email.Subject,
      htmlContent = email.HtmlBody,
      textContent = email.TextBody
    };

    using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
    {
      Content = JsonContent.Create(requestBody)
    };

    request.Headers.TryAddWithoutValidation("api-key", _options.ApiKey);
    request.Headers.Accept.ParseAdd("application/json");

    using var response = await httpClient.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        ct);

    if (!response.IsSuccessStatusCode)
    {
      var responseBody = await response.Content.ReadAsStringAsync(ct);

      throw new HttpRequestException(
          "Brevo email request failed. " +
          $"Status: {(int)response.StatusCode} ({response.StatusCode}). " +
          $"Response: {responseBody}",
          null,
          response.StatusCode);
    }
  }
}