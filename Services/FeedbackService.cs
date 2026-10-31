using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Services;

public sealed class FeedbackService(MyShoppingListDbContext db, IOptions<AdminOptions> admins, IOptions<OperationsOptions> options, TimeProvider clock)
{
    public static readonly string[] States = ["new", "triaged", "planned", "resolved", "closed"];
    private static FeedbackView View(FeedbackTicket t, bool admin = false) => new(t.Id, t.UserAccountId, t.Category, t.Message, t.Rating, t.Component,
        t.Status, t.Response, admin ? t.IssueReference : null, t.Screenshot != null, t.Version, t.CreatedAt, t.UpdatedAt);
    private static IQueryable<FeedbackView> Views(IQueryable<FeedbackTicket> query, bool admin) => query.Select(t => new FeedbackView(
        t.Id, t.UserAccountId, t.Category, t.Message, t.Rating, t.Component, t.Status, t.Response, admin ? t.IssueReference : null,
        t.Screenshot != null, t.Version, t.CreatedAt, t.UpdatedAt));
    private void Admin(long id) { if (!admins.Value.AccountIds.Contains(id)) throw new UserImportException(403, "Administrator access required."); }
    public async Task<FeedbackView> SubmitAsync(long account, FeedbackSubmission request, CancellationToken ct)
    {
        if (request.ExpectedAccountId != account) throw new UserImportException(409, "Your account changed. Review your feedback before sending.");
        if (request.RequestId == Guid.Empty || request.Category is not ("bug" or "suggestion" or "other")
            || string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 5000 || request.Message.Contains('\0')
            || request.Rating is < 1 or > 5 || request.Component is not ("website" or "extension"))
            throw new UserImportException(400, "Choose a feedback type and enter a message of at most 5000 characters.");
        if (request.Screenshot is { } image && (!request.ScreenshotApproved || !Png(image)))
            throw new UserImportException(400, "Approve a PNG screenshot of at most 500 KB and 4096 × 4096 pixels, or remove it.");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var owner = await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id"={account} FOR UPDATE""").SingleOrDefaultAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (owner is null || !owner.IsActive || owner.IsTrial && owner.ExpiresDate <= now) throw new UserImportException(403, "Sign in to send feedback.");
        var previous = await db.FeedbackTickets.SingleOrDefaultAsync(t => t.UserAccountId == account && t.RequestId == request.RequestId, ct);
        if (previous is not null)
        {
            if (previous.RequestHash != hash) throw new UserImportException(409, "This feedback request was already sent with different content.");
            return View(previous);
        }
        if (await db.FeedbackTickets.CountAsync(t => t.UserAccountId == account && t.CreatedAt > now.AddHours(-1), ct) >= 10)
            throw new UserImportException(429, "You have sent several messages. Please wait before sending more feedback.");
        var ticket = new FeedbackTicket { UserAccountId = account, RequestId = request.RequestId, RequestHash = hash,
            Category = request.Category, Message = request.Message.Trim(), Rating = request.Rating, Component = request.Component,
            Release = options.Value.Release, Screenshot = request.Screenshot, CreatedAt = now, UpdatedAt = now };
        db.FeedbackTickets.Add(ticket); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return View(ticket);
    }
    // Serve only as an authenticated attachment with nosniff; no uploaded markup.
    private static bool Png(byte[] image) => image.Length is >= 33 and <= 500000
        && image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
        && image.AsSpan(12, 4).SequenceEqual("IHDR"u8)
        && BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(16, 4)) is > 0 and <= 4096
        && BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(20, 4)) is > 0 and <= 4096;

    public Task<FeedbackView[]> OwnAsync(long account, CancellationToken ct) => Views(db.FeedbackTickets.AsNoTracking()
        .Where(t => t.UserAccountId == account).OrderByDescending(t => t.CreatedAt).Take(50), false).ToArrayAsync(ct);
    public async Task<FeedbackView[]> InboxAsync(long admin, string? state, CancellationToken ct)
    {
        Admin(admin); if (state is not null && !States.Contains(state)) throw new UserImportException(400, "Choose a valid feedback status.");
        Audit(admin, "feedback_inbox", null, "Support triage"); await db.SaveChangesAsync(ct);
        return await Views(db.FeedbackTickets.AsNoTracking().Where(t => state == null || t.Status == state)
            .OrderByDescending(t => t.CreatedAt).Take(100), true).ToArrayAsync(ct);
    }
    public async Task<FeedbackView> UpdateAsync(long admin, Guid id, FeedbackUpdate request, CancellationToken ct)
    {
        Admin(admin);
        if (!States.Contains(request.Status) || (request.Response?.Length ?? 0) > 2000 || (request.IssueReference?.Length ?? 0) > 120
            || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000) throw new UserImportException(400, "Choose a status and enter a review reason.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = await db.FeedbackTickets.FromSqlInterpolated($"""SELECT * FROM "FeedbackTickets" WHERE "Id"={id} FOR UPDATE""").SingleOrDefaultAsync(ct)
            ?? throw new UserImportException(404, "Feedback not found.");
        if (request.ExpectedVersion != ticket.Version) throw new UserImportException(409, "This ticket changed. Refresh before saving.");
        ticket.Status = request.Status; ticket.Response = string.IsNullOrWhiteSpace(request.Response) ? null : request.Response.Trim();
        ticket.IssueReference = string.IsNullOrWhiteSpace(request.IssueReference) ? null : request.IssueReference.Trim();
        ticket.Version++; ticket.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        Audit(admin, "feedback_update", id.ToString(), request.Reason.Trim());
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return View(ticket, true);
    }
    public async Task<byte[]?> ScreenshotAsync(long actor, Guid id, bool administrator, CancellationToken ct)
    {
        if (administrator) Admin(actor);
        var image = await db.FeedbackTickets.AsNoTracking().Where(t => t.Id == id && (administrator || t.UserAccountId == actor)).Select(t => t.Screenshot).SingleOrDefaultAsync(ct);
        if (administrator && image != null) { Audit(actor, "feedback_screenshot", id.ToString(), "Support screenshot review"); await db.SaveChangesAsync(ct); }
        return image;
    }
    private void Audit(long admin, string action, string? subject, string reason) => db.OperationsAdminAudits.Add(new()
        { AdministratorId = admin, Action = action, Subject = subject, Reason = reason, CreatedAt = clock.GetUtcNow().UtcDateTime });
}
