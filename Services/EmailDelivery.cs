using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;

namespace myshoppinglist_api.Services;

public sealed record OutboundEmail(string[] Recipients, string Subject, string Body);
public interface IEmailTransport
{
    Task SendAsync(Guid messageId, OutboundEmail email, CancellationToken ct);
}

public sealed class SmtpEmailTransport(IOptions<EmailDeliveryOptions> options) : IEmailTransport
{
    public async Task SendAsync(Guid messageId, OutboundEmail email, CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.Enabled) throw new InvalidOperationException("Email delivery is disabled.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        using var message = new MimeMessage();
        var sender = MailboxAddress.Parse(settings.FromAddress);
        message.From.Add(sender);
        message.MessageId = messageId.ToString("N") + "@" + sender.Domain;
        message.Subject = email.Subject;
        foreach (var recipient in email.Recipients)
            (email.Recipients.Length == 1 ? message.To : message.Bcc).Add(MailboxAddress.Parse(recipient));
        message.Body = new TextPart("plain") { Text = email.Body };
        using var client = new SmtpClient { Timeout = settings.TimeoutSeconds * 1000 };
        // No protocol logger, certificate bypass, opportunistic TLS or automatic fallback.
        await client.ConnectAsync(settings.Host, settings.Port, settings.AllowLoopbackWithoutTls ? SecureSocketOptions.None
            : settings.ImplicitTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, timeout.Token);
        if (!settings.AllowLoopbackWithoutTls && !client.IsSecure) throw new InvalidOperationException("Secure SMTP is required.");
        if (settings.Username.Length > 0) await client.AuthenticateAsync(settings.Username, settings.Password, timeout.Token);
        await client.SendAsync(message, timeout.Token);
        await client.DisconnectAsync(true, timeout.Token);
    }

    public static bool Valid(EmailDeliveryOptions o, bool development)
    {
        bool Address(string value) => AccountAuthService.NormaliseEmail(value) is not null;
        return !o.Enabled || o.Host.Length is > 0 and <= 253 && !o.Host.Any(char.IsWhiteSpace)
            && Uri.CheckHostName(o.Host) != UriHostNameType.Unknown && Address(o.FromAddress)
            && o.Port is >= 1 and <= 65535 && o.TimeoutSeconds is >= 1 and <= 30 && o.MaximumAttempts is >= 1 and <= 5
            && (o.Username.Length == 0 && o.Password.Length == 0 || o.Username.Length is > 0 and <= 320 && o.Password.Length is > 0 and <= 4096)
            && (!o.AllowLoopbackWithoutTls || development && IPAddress.TryParse(o.Host, out var ip) && IPAddress.IsLoopback(ip)
                && o.Username.Length == 0 && o.Password.Length == 0);
    }
}
