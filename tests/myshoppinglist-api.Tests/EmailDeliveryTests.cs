using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using MimeKit;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

public sealed class EmailDeliveryTests
{
    [Theory]
    [InlineData("127.0.0.1", true, "", true)]
    [InlineData("127.0.0.1", false, "", false)]
    [InlineData("smtp.example.test", true, "", false)]
    [InlineData("localhost", true, "", false)]
    [InlineData("127.0.0.1", true, "username", false)]
    public void Plaintext_smtp_is_limited_to_explicit_credential_free_development_loopback(string host, bool development, string username, bool valid)
    {
        var options = Settings(); options.Host = host; options.AllowLoopbackWithoutTls = true; options.Username = username;
        if (username.Length > 0) options.Password = "not-a-real-password";
        Assert.Equal(valid, SmtpEmailTransport.Valid(options, development));
    }

    [Fact]
    public async Task Transport_sends_only_to_the_local_smtp_fixture_and_hides_multiple_recipients()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var recipients = new List<string>(); var data = new StringBuilder();
        var smtp = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
            await using var stream = client.GetStream(); using var reader = new StreamReader(stream);
            await using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
            await writer.WriteLineAsync("220 local-fixture ESMTP");
            while (await reader.ReadLineAsync(cancellation.Token) is { } line)
            {
                if (line.StartsWith("EHLO", StringComparison.Ordinal)) { await writer.WriteLineAsync("250-local-fixture"); await writer.WriteLineAsync("250 SIZE 100000"); }
                else if (line.StartsWith("MAIL FROM:", StringComparison.Ordinal)) await writer.WriteLineAsync("250 sender accepted");
                else if (line.StartsWith("RCPT TO:", StringComparison.Ordinal)) { recipients.Add(line); await writer.WriteLineAsync("250 recipient accepted"); }
                else if (line == "DATA")
                {
                    await writer.WriteLineAsync("354 send data");
                    while (await reader.ReadLineAsync(cancellation.Token) is { } content && content != ".") data.AppendLine(content.StartsWith("..", StringComparison.Ordinal) ? content[1..] : content);
                    await writer.WriteLineAsync("250 queued locally");
                }
                else if (line == "QUIT") { await writer.WriteLineAsync("221 closing"); break; }
                else throw new InvalidOperationException("Unexpected SMTP fixture command.");
            }
        }, cancellation.Token);
        var settings = Settings(); settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port; settings.AllowLoopbackWithoutTls = true;
        var transport = new SmtpEmailTransport(Options.Create(settings));
        var id = Guid.NewGuid();
        await transport.SendAsync(id, new(["first@example.test", "second@example.test"], "Local fixture only", "Fixed incident reference: fixture"), cancellation.Token);
        await smtp;
        Assert.Equal(2, recipients.Count);
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(data.ToString())); using var message = await MimeMessage.LoadAsync(bytes, cancellation.Token);
        Assert.Equal("Local fixture only", message.Subject); Assert.Equal("Fixed incident reference: fixture", message.TextBody!.Trim());
        Assert.Equal(id.ToString("N") + "@example.test", message.MessageId);
        Assert.DoesNotContain("first@example.test", data.ToString()); Assert.DoesNotContain("second@example.test", data.ToString());
    }

    [Fact]
    public async Task Mandatory_starttls_never_falls_back_to_plaintext_when_the_server_does_not_offer_tls()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        string? greeting = null;
        var smtp = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
            await using var stream = client.GetStream(); using var reader = new StreamReader(stream);
            await using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
            await writer.WriteLineAsync("220 local-fixture ESMTP"); greeting = await reader.ReadLineAsync(cancellation.Token);
            await writer.WriteLineAsync("250-local-fixture"); await writer.WriteLineAsync("250 SIZE 100000");
            Assert.Null(await reader.ReadLineAsync(cancellation.Token));
        }, cancellation.Token);
        var settings = Settings(); settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await Assert.ThrowsAsync<NotSupportedException>(() => new SmtpEmailTransport(Options.Create(settings))
            .SendAsync(Guid.NewGuid(), new(["nobody@example.test"], "Must not send", "Private fixture body"), cancellation.Token));
        await smtp; Assert.StartsWith("EHLO ", greeting);
    }

    private static EmailDeliveryOptions Settings() => new() { Enabled = true, Host = "127.0.0.1", FromAddress = "alerts@example.test", TimeoutSeconds = 5 };
}
