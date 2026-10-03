using System.Net;
using System.Net.Mail;

namespace ToolShed.Web.Services;

public interface IEmailService
{
    /// <summary>False when no SMTP host is configured; callers fall back to showing links on screen.</summary>
    bool IsConfigured { get; }

    Task<bool> SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}

public class SmtpOptions
{
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    /// <summary>STARTTLS on the configured port. Leave on unless you are talking to a local relay.</summary>
    public bool UseStartTls { get; set; } = true;

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string? FromAddress { get; set; }

    public string FromName { get; set; } = "The Tool Shed";
}

public class SmtpEmailService : IEmailService
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IConfiguration configuration, ILogger<SmtpEmailService> logger)
    {
        _options = configuration.GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions();
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.Host) && !string.IsNullOrWhiteSpace(_options.FromAddress);

    public async Task<bool> SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return false;
        }

        try
        {
            using var message = new MailMessage(
                new MailAddress(_options.FromAddress!, _options.FromName),
                new MailAddress(to))
            {
                // Subjects can carry member-supplied text (tool names), so never let a line break through.
                Subject = subject.ReplaceLineEndings(" "),
                Body = body,
                IsBodyHtml = false
            };

            using var client = new SmtpClient(_options.Host!, _options.Port)
            {
                EnableSsl = _options.UseStartTls,
                DeliveryMethod = SmtpDeliveryMethod.Network
            };

            if (!string.IsNullOrWhiteSpace(_options.Username))
            {
                client.Credentials = new NetworkCredential(_options.Username, _options.Password);
            }

            await client.SendMailAsync(message, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is SmtpException or FormatException or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "Could not send email to a member.");
            return false;
        }
    }
}
