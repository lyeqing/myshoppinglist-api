using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;

namespace myshoppinglist_api.Security;

public sealed class ExtensionDeviceAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, MyShoppingListDbContext db, TimeProvider clock,
    IOptions<UserExtensionOptions> extensions, IOptions<ExtensionBridgeOptions> bridge)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ExtensionDevice";
    public const string DeviceIdHeader = "X-MyShoppingList-Device";
    public const string DeviceIdClaim = "myshoppinglist:device";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!bridge.Value.Enabled) return AuthenticateResult.NoResult();
        var ids = Request.Headers[DeviceIdHeader];
        var header = Request.Headers.Authorization;
        if (ids.Count != 1 || !Guid.TryParseExact(ids.ToString(), "D", out var id) || id == Guid.Empty
            || header.Count != 1 || !header.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || !extensions.Value.Allows(Request)) return AuthenticateResult.Fail("Invalid device.");
        var secret = header.ToString()[7..];
        if (!SessionToken.IsValidFormat(secret)) return AuthenticateResult.Fail("Invalid device.");
        var hash = SessionToken.Hash(secret);
        var now = clock.GetUtcNow().UtcDateTime;
        var extensionId = Request.Headers["X-MyShoppingList-Extension"].ToString();
        var device = await db.ExtensionDevices.AsNoTracking().Where(d => d.Id == id && d.TokenHash == hash && d.RevokedAt == null
            && d.ExtensionId == extensionId
            && d.UserAccount.IsActive && !d.UserAccount.IsTrial && d.UserSession.RevokedDate == null && d.UserSession.ExpiresDate > now)
            .Select(d => new { d.Id, d.UserAccountId }).SingleOrDefaultAsync(Context.RequestAborted);
        if (device is null) return AuthenticateResult.Fail("Invalid device.");
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, device.UserAccountId.ToString(CultureInfo.InvariantCulture)),
            new Claim(DeviceIdClaim, device.Id.ToString()), new Claim(SessionTokenAuthenticationHandler.TransportClaim, "bearer")], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        Results.Problem(statusCode: 401, title: "Link this extension again to continue.").ExecuteAsync(Context);
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Results.Problem(statusCode: 403, title: "This device cannot perform that action.").ExecuteAsync(Context);
}
