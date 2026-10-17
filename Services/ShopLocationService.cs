using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Services;

public sealed class ShopLocationService(MyShoppingListDbContext db, TimeProvider clock, IOptions<AdminOptions> administrators)
{
    private async Task AuthorizeAsync(long accountId, bool administrator, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!await db.UserAccounts.AnyAsync(a => a.Id == accountId && a.IsActive
            && (!a.IsTrial || a.ExpiresDate > now), ct))
            throw new UserImportException(401, "Your session is unavailable.");
        if (administrator && (!administrators.Value.AccountIds.Contains(accountId)
            || !await db.UserAccounts.AnyAsync(a => a.Id == accountId && !a.IsTrial, ct)))
            throw new UserImportException(403, "Administrator access required.");
    }

    private static string Text(string? value, string field, int length, bool optional = false)
    {
        var result = value?.Trim() ?? "";
        if ((!optional && result.Length == 0) || (value?.Length ?? 0) > length || result.Any(char.IsControl))
            throw new UserImportException(400, $"Enter valid {field} of at most {length} characters.");
        return result;
    }
    private static ShopLocationDetails Validate(ShopLocationDetails? input)
    {
        if (input is null || input.ShopId <= 0) throw new UserImportException(400, "Select a valid retailer and supply branch details.");
        var state = Text(input.State, "state", 100).ToUpperInvariant();
        if (!new[] { "ACT", "NSW", "NT", "QLD", "SA", "TAS", "VIC", "WA" }.Contains(state))
            throw new UserImportException(400, "Select an Australian state or territory.");
        var postcode = Text(input.Postcode, "postcode", 20);
        if (postcode.Length != 4 || !postcode.All(char.IsAsciiDigit))
            throw new UserImportException(400, "Enter a four-digit Australian postcode.");
        return input with
        {
            StoreCode = Text(input.StoreCode, "retailer branch code", 100),
            Name = Text(input.Name, "branch name", 200),
            Address1 = Text(input.Address1, "street address", 300),
            Address2 = Text(input.Address2, "additional address", 300, true),
            Suburb = Text(input.Suburb, "suburb", 100),
            State = state,
            Postcode = postcode
        };
    }
    private static string DirectoryKey(string? address1, string? address2, string? suburb, string? postcode)
    {
        var normalisation = new ProductNormalisationService();
        var value = normalisation.Text($"{address1} {address2}") + "|" + normalisation.Text(suburb) + "|" + postcode?.Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
    private static DateTime StorageTime(DateTime date) => new(date.Ticks / 10 * 10, DateTimeKind.Utc);
    private static ShopLocationSummary Summary(ShopLocation branch) => new(branch.Id, branch.ShopId, branch.Shop.Name,
        branch.StoreCode, branch.Name, branch.Address1, branch.Address2, branch.Suburb, branch.State,
        branch.Postcode, branch.VerifiedAt);
    private static ShopLocationAdminSummary AdminSummary(ShopLocation branch) => new(Summary(branch), branch.IsActive,
        branch.SourceReference, branch.VerifiedByAccountId, branch.CreatedDate, branch.UpdatedDate);
    private static string Snapshot(ShopLocation branch) => JsonSerializer.Serialize(new
    {
        branch.Id,
        branch.ShopId,
        branch.StoreCode,
        branch.Name,
        branch.Address1,
        branch.Address2,
        branch.Suburb,
        branch.State,
        branch.Postcode,
        branch.Latitude,
        branch.Longitude,
        branch.IsActive,
        branch.SourceReference,
        branch.VerifiedAt,
        branch.VerifiedByAccountId,
        branch.CreatedDate,
        branch.UpdatedDate
    });

    private async Task<ShopLocation[]> SearchCoreAsync(long? shopId, string? search, bool administrator, CancellationToken ct)
    {
        var term = search?.Trim() ?? "";
        if (shopId is <= 0 || (search?.Length ?? 0) > 100 || term.Any(char.IsControl)
            || shopId is null && term.Length < 2 || term.Length == 1)
            throw new UserImportException(400, "Select a retailer or search with at least two characters, using at most 100 characters.");
        if (shopId is not null && !await db.Shops.AnyAsync(s => s.Id == shopId && (administrator || s.IsActive), ct))
            throw new UserImportException(400, "The selected retailer is unavailable.");
        var query = db.ShopLocations.AsNoTracking().Include(l => l.Shop).AsQueryable();
        if (!administrator)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            query = query.Where(l => l.IsActive && l.Shop.IsActive && l.VerifiedAt != null && l.VerifiedAt <= now
                && l.VerifiedByAccountId != null && l.SourceReference != null && l.DirectoryKey != null);
        }
        if (shopId is not null) query = query.Where(l => l.ShopId == shopId);
        if (term.Length > 0)
        {
            var lower = term.ToLowerInvariant();
            query = query.Where(l => l.Name.ToLower().Contains(lower) || l.Shop.Name.ToLower().Contains(lower)
                || l.Suburb != null && l.Suburb.ToLower().Contains(lower) || l.Postcode != null && l.Postcode.Contains(term));
        }
        return await query.OrderBy(l => l.Name).ThenBy(l => l.Suburb).ThenBy(l => l.Id).Take(51).ToArrayAsync(ct);
    }
    public async Task<ShopLocationSearchResult> SearchAsync(long accountId, long? shopId, string? search, CancellationToken ct)
    {
        await AuthorizeAsync(accountId, false, ct);
        var found = await SearchCoreAsync(shopId, search, false, ct);
        return new(found.Take(50).Select(Summary).ToArray(), found.Length > 50);
    }
    public async Task<ShopLocationAdminSearchResult> SearchAdminAsync(long administrator, long? shopId, string? search, CancellationToken ct)
    {
        await AuthorizeAsync(administrator, true, ct);
        var found = await SearchCoreAsync(shopId, search, true, ct);
        return new(found.Take(50).Select(AdminSummary).ToArray(), found.Length > 50);
    }
    public async Task<ShopLocationSummary> FindAsync(long accountId, long id, CancellationToken ct)
    {
        await AuthorizeAsync(accountId, false, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var branch = await db.ShopLocations.AsNoTracking().Include(l => l.Shop).SingleOrDefaultAsync(l => l.Id == id
            && l.IsActive && l.Shop.IsActive && l.VerifiedAt != null && l.VerifiedAt <= now
            && l.VerifiedByAccountId != null && l.SourceReference != null && l.DirectoryKey != null, ct)
            ?? throw new UserImportException(404, "Verified branch not found.");
        return Summary(branch);
    }
    public async Task<ShopLocationAdminDetail> ReadAdminAsync(long administrator, long id, CancellationToken ct)
    {
        await AuthorizeAsync(administrator, true, ct);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var branch = await db.ShopLocations.AsNoTracking().Include(l => l.Shop).SingleOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new UserImportException(404, "Branch not found.");
        var reviews = await db.ShopLocationReviews.AsNoTracking().Where(r => r.ShopLocationId == id).OrderByDescending(r => r.Id)
            .Take(100).Select(r => new ShopLocationReviewResponse(r.Id, r.Action, r.Evidence, r.SourceReference,
                r.ReviewerAccountId, r.ReviewedAt, r.BeforeJson, r.AfterJson)).ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return new(AdminSummary(branch), reviews);
    }
    public Task<ShopLocationAdminDetail> CreateAsync(long administrator, ShopLocationCreateRequest request, CancellationToken ct) =>
        WriteAsync(administrator, null, request.Details, request.SourceReference, request.Evidence, null, ct);
    public Task<ShopLocationAdminDetail> UpdateAsync(long administrator, long id, ShopLocationUpdateRequest request, CancellationToken ct) =>
        WriteAsync(administrator, id, request.Details, request.SourceReference, request.Evidence, request.ExpectedUpdatedDate, ct);

    private async Task<ShopLocationAdminDetail> WriteAsync(long administrator, long? id, ShopLocationDetails? input,
        string? reference, string? evidence, DateTime? expectedUpdatedDate, CancellationToken ct)
    {
        await AuthorizeAsync(administrator, true, ct);
        var details = Validate(input);
        var source = Text(reference, "verification source reference", 1000);
        var reason = evidence?.Trim() ?? "";
        if (reason.Length == 0 || (evidence?.Length ?? 0) > 2000)
            throw new UserImportException(400, "Record verification evidence or a change reason of at most 2000 characters.");
        if (id is not null && (expectedUpdatedDate is null || expectedUpdatedDate == default(DateTime)
            || expectedUpdatedDate.Value.Kind != DateTimeKind.Utc))
            throw new UserImportException(400, "Send the UTC UpdatedDate returned with the branch.");
        long branchId;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            // Same lock as price writes: branch identity cannot change under a price save.
            await ProductService.LockCatalogueAsync(db, ct);
            await AuthorizeAsync(administrator, true, ct);
            var shop = await db.Shops.AsNoTracking().SingleOrDefaultAsync(s => s.Id == details.ShopId, ct)
                ?? throw new UserImportException(400, "Retailer not found.");
            if (!shop.IsActive && (id is null || details.IsActive))
                throw new UserImportException(400, "An inactive retailer cannot publish a branch.");
            ShopLocation? branch = null;
            if (id is not null)
            {
                branch = await db.ShopLocations.SingleOrDefaultAsync(l => l.Id == id, ct)
                    ?? throw new UserImportException(404, "Branch not found.");
                // Reload if this context previously tracked the branch before another writer.
                await db.Entry(branch).ReloadAsync(ct);
                if (branch.UpdatedDate != expectedUpdatedDate)
                    throw new UserImportException(409, "This branch changed. Reload it before saving.");
                if (branch.ShopId != details.ShopId || branch.StoreCode is not null && branch.StoreCode != details.StoreCode)
                    throw new UserImportException(400, "Retailer and existing branch code are stable identities. Retire an incorrect record instead of moving it.");
            }
            var key = DirectoryKey(details.Address1, details.Address2, details.Suburb, details.Postcode);
            var code = details.StoreCode!.ToLowerInvariant();
            if (await db.ShopLocations.AnyAsync(l => l.ShopId == details.ShopId && l.Id != (id ?? 0)
                && (l.StoreCode != null && l.StoreCode.Trim().ToLower() == code || details.IsActive && l.IsActive && l.DirectoryKey == key), ct))
                throw new UserImportException(409, "This retailer branch already exists. Review or reactivate the existing record.");
            // Legacy rows may not have a directory key. Compare their supplied address
            // without granting them verification or modifying their stored identity.
            var legacy = await db.ShopLocations.AsNoTracking().Where(l => details.IsActive && l.IsActive && l.ShopId == details.ShopId && l.Id != (id ?? 0)
                && l.DirectoryKey == null && l.Postcode != null && l.Postcode.Trim() == details.Postcode).ToArrayAsync(ct);
            if (legacy.Any(l => DirectoryKey(l.Address1, l.Address2, l.Suburb, l.Postcode) == key))
                throw new UserImportException(409, "A saved branch has this address. Review the existing record.");
            var now = StorageTime(clock.GetUtcNow().UtcDateTime);
            var before = branch is null ? null : Snapshot(branch);
            var action = branch is null ? "Created" : branch.IsActive && !details.IsActive ? "Retired"
                : !branch.IsActive && details.IsActive ? "Reactivated" : "Updated";
            if (branch is null)
            {
                branch = new() { ShopId = details.ShopId, CreatedDate = now };
                db.ShopLocations.Add(branch);
            }
            branch.StoreCode = details.StoreCode; branch.Name = details.Name!; branch.Address1 = details.Address1;
            branch.Address2 = string.IsNullOrEmpty(details.Address2) ? null : details.Address2; branch.Suburb = details.Suburb;
            branch.State = details.State; branch.Postcode = details.Postcode; branch.IsActive = details.IsActive;
            branch.DirectoryKey = key; branch.SourceReference = source; branch.VerifiedAt = now; branch.VerifiedByAccountId = administrator;
            branch.UpdatedDate = new DateTime(Math.Max(now.Ticks, branch.UpdatedDate.Ticks + 10), DateTimeKind.Utc);
            await db.SaveChangesAsync(ct);
            db.ShopLocationReviews.Add(new()
            {
                ShopLocationId = branch.Id,
                ReviewerAccountId = administrator,
                Action = action,
                Evidence = reason,
                SourceReference = source,
                BeforeJson = before,
                AfterJson = Snapshot(branch),
                ReviewedAt = now
            });
            await db.SaveChangesAsync(ct);
            branchId = branch.Id;
            await transaction.CommitAsync(ct);
        }
        return await ReadAdminAsync(administrator, branchId, ct);
    }
}
