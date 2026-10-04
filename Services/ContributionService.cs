using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Services;

public sealed class ContributionService(MyShoppingListDbContext db, ColesExtensionTaskService queue, TimeProvider clock)
{
    public async Task<UserAccount> AccountAsync(long id, CancellationToken ct) =>
        await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.IsActive && !a.IsTrial, ct)
        ?? throw new UserImportException(403, "An active registered account is required.");

    public async Task<ColesTaskClaim?> ClaimAsync(long id, CancellationToken ct)
    {
        var a = await AccountAsync(id, ct);
        if (a.ContributionBlocked || !a.ContributionEnabled) return null;
        var worker = "customer-" + id;
        var existing = await db.ColesExtensionTasks.AsNoTracking().Where(t => t.Status == "Processing" && t.ContributorAccountId == id)
            .Select(t => (long?)t.Id).FirstOrDefaultAsync(ct);
        if (existing is { } active) {
            var claim = await queue.ClaimAsync(active, worker, ct, id);
            if (claim is not null) return claim;
        }
        foreach (var task in (await queue.ListAsync(ct)).Where(t => t.Status == "Waiting").Take(10)) {
            var claim = await queue.ClaimAsync(task.Id, worker, ct, id);
            if (claim is not null) return claim;
        }
        return null;
    }

    public async Task SubmitAsync(long account, long task, ContributionResult result, CancellationToken ct)
    {
        var a = await AccountAsync(account, ct);
        if (a.ContributionBlocked || !a.ContributionEnabled) throw new UserImportException(403, "Contribution is disabled.");
        var claim = await db.ColesExtensionTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == task && t.ContributorAccountId == account
            && t.ClaimToken == result.ClaimToken, ct) ?? throw new UserImportException(409, "Task reservation expired.");
        if (result.Url != claim.Url) throw new UserImportException(400, "Task URL does not match.");
        var observation = new ContributionObservation { UserAccountId = account, TaskId = task, Source = "SharedCustomer",
            Url = claim.Url, ReceivedAt = clock.GetUtcNow().UtcDateTime, CollectedAt = claim.ClaimedAt!.Value,
            ExtensionVersion = Version(result.ExtensionVersion) };
        db.ContributionObservations.Add(observation); await db.SaveChangesAsync(ct); db.ChangeTracker.Clear();
        var error = await queue.SubmitAsync(task, new("customer-" + account, result.ClaimToken, result.Url, result.Ok,
            result.Evidence, result.Links, result.EmptyConfirmed, result.ErrorCode), ct, account, observation.Id);
        db.ChangeTracker.Clear();
        await db.ContributionObservations.Where(o => o.Id == observation.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Outcome,
            error ?? (claim.SubmissionHash != null ? "Duplicate" : result.Ok ? "Accepted" : "ExtractionFailed")), ct);
        if (error is not null) throw new UserImportException(error == "claim_lost" ? 409 : 400, error);
    }
    public static string Version(string? value) => value is { Length: > 0 and <= 40 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ? value : "unknown";
}
