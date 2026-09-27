using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;

namespace myshoppinglist_api.Security;

public sealed class ColesWorkerAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IOptionsMonitor<ColesExtensionOptions> workers)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ColesWorker";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = workers.CurrentValue.WorkerKey;
        var header = Request.Headers.Authorization;
        var supplied = header.ToString();
        if (key.Length < 32 || header.Count != 1 || !supplied.StartsWith("Bearer ", StringComparison.Ordinal)
            || supplied.Length > 1024 || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied[7..])), SHA256.HashData(Encoding.UTF8.GetBytes(key))))
            return Task.FromResult(AuthenticateResult.Fail("Invalid worker credential."));
        var identity = new ClaimsIdentity(new[] { new Claim("coles_worker", "true"),
            new Claim(SessionTokenAuthenticationHandler.TransportClaim, "bearer") }, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), SchemeName)));
    }
}
