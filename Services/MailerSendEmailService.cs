using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BakeSmartPatri.Services;

public interface IEmailService
{
    bool IsConfigured { get; }
    Task SendAsync(string toEmail, string toName, string subject, string text, string? html = null, CancellationToken cancellationToken = default);
}

public sealed class BrevoEmailService : IEmailService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BrevoEmailService> _logger;

    public BrevoEmailService(HttpClient httpClient, IConfiguration configuration, ILogger<BrevoEmailService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_configuration["Brevo:ApiKey"]) &&
        !string.IsNullOrWhiteSpace(_configuration["Brevo:FromEmail"]);

    public async Task SendAsync(string toEmail, string toName, string subject, string text, string? html = null, CancellationToken cancellationToken = default)
    {
        var apiToken = _configuration["Brevo:ApiKey"];
        var fromEmail = _configuration["Brevo:FromEmail"];
        var fromName = _configuration["Brevo:FromName"] ?? "Reposteria Patri";

        if (string.IsNullOrWhiteSpace(apiToken) || string.IsNullOrWhiteSpace(fromEmail))
            throw new InvalidOperationException("El servicio de correo todavía no está configurado.");

        var payload = new
        {
            sender = new { email = fromEmail.Trim(), name = fromName.Trim() },
            to = new[] { new { email = toEmail.Trim(), name = string.IsNullOrWhiteSpace(toName) ? toEmail.Trim() : toName.Trim() } },
            subject = subject.Trim(),
            textContent = text.Trim(),
            htmlContent = string.IsNullOrWhiteSpace(html) ? BuildHtml(subject, text) : html
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "smtp/email");
        request.Headers.Add("api-key", apiToken.Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Correo aceptado por Brevo para {Recipient}", toEmail);
            return;
        }

        var providerMessage = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogError("Brevo rechazó el correo para {Recipient}. Estado {Status}: {Message}", toEmail, response.StatusCode, providerMessage);
        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Brevo rechazó las credenciales configuradas.",
            HttpStatusCode.BadRequest => "Brevo rechazó el remitente o el contenido del correo.",
            HttpStatusCode.TooManyRequests => "Brevo alcanzó temporalmente el límite de envíos.",
            _ => "No fue posible enviar el correo mediante Brevo."
        });
    }

    public static string BuildHtml(string title, string message)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        var safeMessage = WebUtility.HtmlEncode(message).Replace("\r\n", "<br>").Replace("\n", "<br>");
        return $$"""
        <!doctype html>
        <html lang="es"><body style="margin:0;background:#f7f3fb;font-family:Arial,sans-serif;color:#25233a">
          <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#f7f3fb;padding:28px 12px">
            <tr><td align="center">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:620px;background:#fff;border-radius:20px;overflow:hidden;border:1px solid #eadff4">
                <tr><td style="padding:24px 30px;background:linear-gradient(135deg,#7138df,#c7488a);color:#fff"><div style="font-size:13px;font-weight:700;letter-spacing:.08em">REPOSTERÍA PATRI</div><h1 style="margin:8px 0 0;font-size:26px">{{safeTitle}}</h1></td></tr>
                <tr><td style="padding:30px;font-size:16px;line-height:1.65">{{safeMessage}}<p style="margin:28px 0 0;color:#777;font-size:13px">Este correo fue enviado por Repostería Patri.</p></td></tr>
              </table>
            </td></tr>
          </table>
        </body></html>
        """;
    }
}
