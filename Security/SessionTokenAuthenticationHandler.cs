using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Data;

namespace myshoppinglist_api.Security;

public sealed class SessionTokenAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, MyShoppingListDbContext db, TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "SessionToken";
    public const string SessionIdClaim = "myshoppinglist:session";
    public const string TransportClaim = "myshoppinglist:transport";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? token;
        var bearer = Request.Headers.ContainsKey("Authorization");
        if (bearer)
        {
            var values = Request.Headers.Authorization;
            var header = values.ToString();
            if (values.Count != 1 || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return AuthenticateResult.Fail("Invalid session.");
            token = header[7..];
        }
        else if (!Request.Cookies.TryGetValue(SessionCookie.Name, out token)) return AuthenticateResult.NoResult();
        // An invalid explicit Authorization header never falls back to a valid browser cookie.
        if (!SessionToken.IsValidFormat(token)) return AuthenticateResult.Fail("Invalid session.");
        var hash = SessionToken.Hash(token!);
        var now = clock.GetUtcNow().UtcDateTime;
        var session = await db.UserSessions.AsNoTracking().Where(s => s.TokenHash == hash && s.RevokedDate == null
            && s.ExpiresDate > now && s.UserAccount.IsActive
            && (!s.UserAccount.IsTrial || s.UserAccount.ExpiresDate > now))
            .Select(s => new { s.Id, s.UserAccountId }).SingleOrDefaultAsync(Context.RequestAborted);
        if (session is null) return AuthenticateResult.Fail("Invalid session.");
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, session.UserAccountId.ToString(CultureInfo.InvariantCulture)),
            new Claim(SessionIdClaim, session.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(TransportClaim, bearer ? "bearer" : "cookie")
        }, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        Results.Problem(statusCode: 401, title: "A valid session is required.").ExecuteAsync(Context);
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Results.Problem(statusCode: 403, title: "Access denied.").ExecuteAsync(Context);
}
