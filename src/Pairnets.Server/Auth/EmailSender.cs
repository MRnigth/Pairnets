using System.Net;
using System.Net.Mail;

namespace Pairnets.Server.Auth;

/// <summary>Sends the nest's emails (sign-in links). Replaceable in tests.</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string textBody, string htmlBody, CancellationToken ct = default);
}

/// <summary>
/// Plain SMTP with STARTTLS on port 587 (or whatever <see cref="SyncOptions.SmtpPort"/> says): works with Resend,
/// Mailgun, Gmail app passwords or a mail server of your own. No other mail dependency.
/// </summary>
public sealed class SmtpEmailSender(SyncOptions options, ILogger<SmtpEmailSender> log) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string textBody, string htmlBody, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.SmtpHost) || string.IsNullOrWhiteSpace(options.SmtpFrom))
            throw new InvalidOperationException("Email is not set up on this nest (Sync:SmtpHost and Sync:SmtpFrom).");
        using var message = new MailMessage { From = new MailAddress(options.SmtpFrom), Subject = subject };
        message.To.Add(new MailAddress(to));
        message.Body = textBody;
        message.IsBodyHtml = false;
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(htmlBody, null, "text/html"));

        using var client = new SmtpClient(options.SmtpHost, options.SmtpPort)
        {
            EnableSsl = options.SmtpUseTls,
            Timeout = 30_000,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        if (!string.IsNullOrEmpty(options.SmtpUser))
            client.Credentials = new NetworkCredential(options.SmtpUser, options.SmtpPassword ?? string.Empty);
        try
        {
            await client.SendMailAsync(message, ct);
        }
        catch (SmtpException ex)
        {
            // The mail server's own words, never the credentials.
            log.LogWarning("Could not send a sign-in email through {Host}:{Port}: {Error}", options.SmtpHost, options.SmtpPort, ex.Message);
            throw;
        }
    }
}
