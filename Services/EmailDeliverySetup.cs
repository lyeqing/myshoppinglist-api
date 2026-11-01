using System.Threading.RateLimiting;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Services;

public static class EmailDeliverySetup
{
    public const string RecoveryRatePolicy = "account-recovery";
    public static void AddAccountSecurity(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var email = configuration.GetSection("EmailDelivery").Get<EmailDeliveryOptions>() ?? new();
        var keyDirectory = configuration["AccountSecurity:KeyDirectory"];
        var certificatePath = configuration["AccountSecurity:KeyCertificatePath"];
        if (email.Enabled && !environment.IsDevelopment() && (string.IsNullOrWhiteSpace(keyDirectory) || string.IsNullOrWhiteSpace(certificatePath)))
            throw new InvalidOperationException("Production email/recovery requires a persistent Data Protection key directory and key-encryption certificate.");
        var protection = services.AddDataProtection().SetApplicationName("MyShoppingList");
        if (!string.IsNullOrWhiteSpace(keyDirectory)) protection.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, configuration["AccountSecurity:KeyCertificatePassword"]);
            using var publicKey = certificate.GetRSAPublicKey();
            if (!certificate.HasPrivateKey || publicKey is null || publicKey.KeySize < 2048)
                throw new InvalidOperationException("Data Protection requires an RSA certificate of at least 2048 bits with a private key.");
            protection.ProtectKeysWithCertificate(certificate);
        }
        services.AddOptions<EmailDeliveryOptions>().BindConfiguration("EmailDelivery")
            .Validate(o => SmtpEmailTransport.Valid(o, environment.IsDevelopment()), "Configure a valid sender and mandatory-TLS SMTP transport. Plaintext is limited to credential-free Development loopback tests.")
            .ValidateOnStart();
        services.AddOptions<AccountRecoveryOptions>().BindConfiguration("AccountRecovery")
            .Validate(o => !o.Enabled || email.Enabled && ValidOrigin(o.WebsiteOrigin, environment.IsDevelopment()),
                "Account recovery requires enabled email delivery and an explicit trusted website origin.").ValidateOnStart();
        services.AddSingleton<RecoveryTokenService>();
        services.AddSingleton<IEmailTransport, SmtpEmailTransport>();
        services.AddScoped<EmailOutboxService>(); services.AddScoped<AccountCredentialService>();
        if (email.Enabled) services.AddHostedService<EmailDeliveryWorker>();
        services.AddRateLimiter(o => o.AddPolicy(RecoveryRatePolicy, c => RateLimitPartition.GetFixedWindowLimiter(
            c.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(15), QueueLimit = 0, AutoReplenishment = true })));
    }

    private static bool ValidOrigin(string value, bool development) => Uri.TryCreate(value, UriKind.Absolute, out var u)
        && u.UserInfo.Length == 0 && u.AbsolutePath == "/" && u.Query.Length == 0 && u.Fragment.Length == 0
        && (u.Scheme == "https" || development && u.Scheme == "http" && u.IsLoopback);
}

public sealed class EmailDeliveryWorker(IServiceScopeFactory scopes, ILogger<EmailDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        var retention = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var outbox = scope.ServiceProvider.GetRequiredService<EmailOutboxService>();
                for (var i = 0; i < 20 && await outbox.DeliverOneAsync(stoppingToken); i++) { }
                if (DateTimeOffset.UtcNow >= retention) { await outbox.RetainAsync(stoppingToken); retention = DateTimeOffset.UtcNow.AddHours(1); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogError("Email outbox processing unavailable; pending messages remain for retry"); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
