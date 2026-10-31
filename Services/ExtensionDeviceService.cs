using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Services;

public sealed class ExtensionDeviceService(MyShoppingListDbContext db, IOptions<ExtensionBridgeOptions> options, TimeProvider clock)
{
    public static readonly string[] Retailers = ["coles", "woolworths", "aldi"];
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static void Version(string? value, int protocol)
    {
        if (protocol != 1) throw new UserImportException(426, "Update the extension to use AI requests.");
        if (value is null || value.Length > 64 || !Regex.IsMatch(value, @"^\d+\.\d+\.\d+(?:\.\d+)?$", RegexOptions.CultureInvariant))
            throw new UserImportException(400, "Send a valid extension version.");
    }

    public async Task<ExtensionDeviceCredentials> RegisterAsync(long accountId, long sessionId, string extensionId,
        ExtensionDeviceRegistration request, CancellationToken ct)
    {
        if (!options.Value.Enabled) throw new UserImportException(503, "AI extension requests are not enabled.");
        if (request.ExpectedAccountId != accountId) throw new UserImportException(409, "Your account changed. Review the browser link again.");
        if (request.InstallationId == Guid.Empty || !Regex.IsMatch(extensionId, "^[a-p]{32}$")) throw new UserImportException(400, "Invalid browser link.");
        Version(request.ExtensionVersion, request.ProtocolVersion);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var account = await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id"={accountId} FOR UPDATE""").SingleOrDefaultAsync(ct);
            if (account is null || !account.IsActive || account.IsTrial) throw new UserImportException(403, "Sign in with a registered account to link AI requests.");
            var now = Now;
            if (!await db.UserSessions.AnyAsync(s => s.Id == sessionId && s.UserAccountId == accountId && s.RevokedDate == null && s.ExpiresDate > now, ct))
                throw new UserImportException(401, "Sign in again to link this browser.");
            var device = await db.ExtensionDevices.SingleOrDefaultAsync(d => d.UserAccountId == accountId && d.InstallationId == request.InstallationId, ct);
            if (device is null)
            {
                if (await db.ExtensionDevices.CountAsync(d => d.UserAccountId == accountId && d.RevokedAt == null && d.UserSession.RevokedDate == null && d.UserSession.ExpiresDate > now, ct) >= options.Value.MaximumDevices)
                    throw new UserImportException(409, "Revoke an old browser link before adding another.");
                device = new() { UserAccountId = accountId, InstallationId = request.InstallationId };
                db.ExtensionDevices.Add(device);
            }
            var secret = SessionToken.Create();
            device.UserSessionId = sessionId; device.ExtensionId = extensionId; device.ExtensionVersion = request.ExtensionVersion;
            device.TokenHash = SessionToken.Hash(secret); device.LinkedAt = now; device.RevokedAt = null;
            device.LastSeenAt = null; device.Interactive = false; device.SupportedRetailers = [];
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(1, device.Id, secret, now);
        }
        finally { db.ChangeTracker.Clear(); }
    }

    public async Task HeartbeatAsync(long accountId, Guid deviceId, ExtensionDeviceHeartbeat request, CancellationToken ct)
    {
        Version(request.ExtensionVersion, request.ProtocolVersion);
        if (request.SupportedRetailers is null || request.SupportedRetailers.Length > 3
            || request.SupportedRetailers.Distinct(StringComparer.Ordinal).Count() != request.SupportedRetailers.Length
            || request.SupportedRetailers.Any(r => !Retailers.Contains(r, StringComparer.Ordinal))) throw new UserImportException(400, "Invalid retailer permissions.");
        var now = Now;
        if (await db.ExtensionDevices.Where(d => d.Id == deviceId && d.UserAccountId == accountId && d.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, now).SetProperty(d => d.Interactive, request.Interactive)
                .SetProperty(d => d.ExtensionVersion, request.ExtensionVersion).SetProperty(d => d.SupportedRetailers, request.SupportedRetailers), ct) != 1)
            throw new UserImportException(401, "Link this browser again.");
    }

    public Task<ExtensionDeviceSummary[]> DevicesAsync(long accountId, CancellationToken ct)
    {
        var now = Now;
        return db.ExtensionDevices.AsNoTracking().Where(d => d.UserAccountId == accountId).OrderByDescending(d => d.LinkedAt)
            .Select(d => new ExtensionDeviceSummary(d.Id, d.ExtensionVersion, d.SupportedRetailers, d.LinkedAt, d.LastSeenAt,
                d.RevokedAt, d.UserSession.RevokedDate == null && d.UserSession.ExpiresDate > now)).ToArrayAsync(ct);
    }

    public async Task<bool> RevokeAsync(long accountId, Guid deviceId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var device = await db.ExtensionDevices.SingleOrDefaultAsync(d => d.Id == deviceId && d.UserAccountId == accountId, ct);
        if (device is null) return false;
        var now = Now;
        device.RevokedAt ??= now; device.Interactive = false;
        await db.ExtensionTasks.Where(t => t.UserAccountId == accountId && t.DeviceId == deviceId && t.Status != "completed"
            && t.Status != "failed" && t.Status != "expired" && t.Status != "cancelled")
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "cancelled").SetProperty(t => t.ErrorCode, "device_revoked").SetProperty(t => t.CompletedAt, now), ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return true;
    }

    public async Task<ExtensionPresence> PresenceAsync(long accountId, CancellationToken ct)
    {
        var now = Now;
        var devices = await db.ExtensionDevices.AsNoTracking().Where(d => d.UserAccountId == accountId && d.RevokedAt == null
            && d.UserSession.RevokedDate == null && d.UserSession.ExpiresDate > now).ToArrayAsync(ct);
        if (devices.Length == 0) return new(1, "not_linked", null, [], "Open the extension, sign in and enable AI requests on your own browser.");
        var seen = devices.Max(d => d.LastSeenAt);
        var recent = devices.Where(d => d.LastSeenAt > now.AddSeconds(-options.Value.PresenceSeconds) && d.LastSeenAt <= now).ToArray();
        if (recent.Length == 0) return new(1, "offline", seen, [], "Open AI requests from the extension toolbar, then ask again. The server cannot switch on a browser.");
        var retailers = recent.SelectMany(d => d.SupportedRetailers).Distinct().Order().ToArray();
        if (retailers.Length == 0) return new(1, "needs_permission", seen, [], "Allow the supported retailer sites in Chrome's extension permissions, then refresh AI requests.");
        if (await db.ExtensionTasks.AnyAsync(t => t.UserAccountId == accountId && t.ExpiresAt > now
            && (t.Status == "queued" || t.Status == "awaiting_user") && t.AiConnection.RevokedDate == null
            && db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization>().Any(a => a.Id == t.AiConnection.AuthorizationId && a.Status == "valid"), ct))
            return new(1, "awaiting_user_approval", seen, retailers,
                "Open AI requests in your own extension to approve or decline pending requests. No page is opened before approval.");
        var interactive = recent.Any(d => d.Interactive && d.LastSeenAt > now.AddSeconds(-15));
        return new(1, interactive ? "ready_for_request" : "recently_seen_unconfirmed", seen, retailers,
            "A browser was recently seen and can receive approval requests when its AI requests page is open. Each request still needs customer approval; execution is not guaranteed.");
    }
}
