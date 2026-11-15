using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Mcp;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;
using Npgsql;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class ResearchTests
{
    // Synthetic evidence: these are fixtures, never actual advertised retailer prices.
    internal static ResearchSubmission Submission() => new(Guid.NewGuid(), "Choose Kleenex toilet tissue", ResearchWorkflow.Version,
        [new("kleenex-1", "coles", "https://www.coles.com.au/product/kleenex-tissue-12345",
            new() { Name = "Kleenex tissue 12 rolls", Brand = "Kleenex", PackQuantity = 12 },
            "Synthetic fixture: 12 rolls, 180 sheets per roll, 3 ply; reported AUD 10.", DateTimeOffset.UtcNow.AddMinutes(-2),
            10, Tissue: new(12, 180, 3, 100, 100))]);

    public static IEnumerable<object[]> GroceryExamples()
    {
        yield return ["Milk 2L", 2000m, "mL", 4m, 0.2m];
        yield return ["Rice 1kg", 1000m, "g", 3m, 0.3m];
        yield return ["Eggs dozen", 12m, "each", 6m, null!];
        yield return ["Apples loose", 1m, "each", 1m, null!];
        yield return ["Detergent 1L", 1000m, "mL", 5m, 0.5m];
        yield return ["Pasta 500g", 500m, "g", 2m, 0.4m];
    }

    [Theory]
    [MemberData(nameof(GroceryExamples))]
    public void Unit_costs_use_dimensions_and_preserve_unknown_basis(string name, decimal size, string unit, decimal price, decimal? expected)
    {
        var input = Submission().Candidates[0] with { Identity = new() { Name = name, PackSize = size, PackUnit = unit }, Tissue = null, Price = price };
        Assert.Equal(expected, ResearchEvidenceValidator.UnitPrice(input).Value);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("retailer")]
    [InlineData("future")]
    [InlineData("negative")]
    [InlineData("currency")]
    [InlineData("gtin")]
    [InlineData("evidence")]
    [InlineData("location")]
    [InlineData("tissue")]
    [InlineData("precision")]
    public void Untrusted_reports_reject_invalid_evidence(string kind)
    {
        var input = Submission().Candidates[0];
        input = kind switch
        {
            "url" => input with { Url = "http://127.0.0.1/private" },
            "retailer" => input with { Retailer = "woolworths" },
            "future" => input with { ObservedAt = DateTimeOffset.UtcNow.AddHours(1) },
            "negative" => input with { Price = -1 },
            "currency" => input with { Currency = "USD" },
            "gtin" => input with { Identity = input.Identity with { GTIN = "12345678" } },
            "evidence" => input with { Evidence = "" },
            "location" => input with { PriceScope = "StoreSpecific" },
            "precision" => input with { Identity = input.Identity with { PackSize = 0.00000000000000000000001m, PackUnit = "g" } },
            _ => input with { Tissue = new(0, 200, 3) }
        };
        var validator = new ResearchEvidenceValidator(new(new RetailerCatalog()), new(), TimeProvider.System);
        Assert.NotEmpty(validator.Validate(input));
    }

    [Fact]
    public void Research_identifiers_round_trip_without_javascript_number_precision_loss()
    {
        const long id = 9007199254740993;
        var report = Submission().Candidates[0] with { RelatedProductId = id, ShopLocationId = id };
        var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("9007199254740993", document.RootElement.GetProperty("relatedProductId").GetString());
        Assert.Equal(id, System.Text.Json.JsonSerializer.Deserialize<ResearchCandidateInput>(json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.ShopLocationId);
    }

    [Fact]
    public async Task Mcp_research_writes_reject_missing_scope_before_using_a_service()
    {
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new(new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier, "1"), new("scope", "lists.read lists.write")], "test")) } };
        var tools = new ResearchWriteTools(null!, http);
        Assert.Equal("insufficient_scope", (await Assert.ThrowsAsync<McpException>(() => tools.Submit(Submission(), default))).Message);
    }

    [PostgreSqlFact]
    public async Task Partial_research_selection_retry_issues_and_isolation_work_without_prices_or_extension()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var app = new AccountSecurityTests.SecurityFactory();
        using var owner = app.Client(); using var other = app.Client(); using var anonymous = app.Client();
        var first = await Register(owner); var second = await Register(other);
        other.DefaultRequestHeaders.Add("X-MyShoppingList-Account", first.Account.Id.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsJsonAsync("/api/research", Submission())).StatusCode);
        other.DefaultRequestHeaders.Remove("X-MyShoppingList-Account");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/research")).StatusCode);
        var request = Submission();
        request = request with { Candidates = [request.Candidates[0], request.Candidates[0] with { Key = "bad", Url = "https://www.coles.com.au/product/invalid-9999", Price = -10 }],
            Issues = [new("woolworths", "retailer_inaccessible", "Synthetic blocked retailer fixture.")] };
        var receipt = await Post<McpMutationResult>(owner, "/api/research", request);
        var id = Guid.Parse(receipt.ResearchJobId!);
        Assert.True((await Post<McpMutationResult>(owner, "/api/research", request)).Replayed);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/research", request with { Query = "Changed inputs" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/research/{id}")).StatusCode);
        var review = (await owner.GetFromJsonAsync<ResearchReview>($"/api/research/{id}"))!;
        Assert.Equal("PartiallyCompleted", review.Status);
        Assert.Single(review.Candidates, c => c.Validation == "Rejected");
        var candidate = Assert.Single(review.Candidates, c => c.Validation == "AcceptedUnverified");
        Assert.Equal(decimal.Round(10m / 2160m * 100, 4), candidate.ComparableUnitPrice);
        Assert.Empty((await owner.GetFromJsonAsync<ShoppingListItemPage>($"/api/shopping-lists/{first.ShoppingListId}/items"))!.Items);
        var choice = new ResearchSelection(Guid.NewGuid(), candidate.Id, first.ShoppingListId!.Value, 3);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/api/research/select", choice with { RequestId = Guid.NewGuid(), ListId = second.ShoppingListId!.Value })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync("/api/research/select", choice with { RequestId = Guid.NewGuid(), ListId = second.ShoppingListId!.Value })).StatusCode);
        var selected = await Post<McpMutationResult>(owner, "/api/research/select", choice);
        Assert.Equal("added", selected.Status);
        Assert.True((await Post<McpMutationResult>(owner, "/api/research/select", choice)).Replayed);
        var item = Assert.Single((await owner.GetFromJsonAsync<ShoppingListItemPage>($"/api/shopping-lists/{first.ShoppingListId}/items"))!.Items);
        Assert.Equal(3, item.Quantity);
        var again = await Post<McpMutationResult>(owner, "/api/research/select", choice with { RequestId = Guid.NewGuid(), Quantity = 7 });
        Assert.Equal("already_on_list", again.Status);
        Assert.Equal(3, (await owner.GetFromJsonAsync<ShoppingListItemPage>($"/api/shopping-lists/{first.ShoppingListId}/items"))!.Items.Single().Quantity);
        var issue = review.Issues.First();
        var update = new ResearchIssueUpdate(Guid.NewGuid(), issue.Revision, "CannotVerify", "Source cannot be verified; keep the limitation visible.");
        (await owner.PutAsJsonAsync($"/api/research/issues/{issue.Id}", update)).EnsureSuccessStatusCode();
        Assert.True((await (await owner.PutAsJsonAsync($"/api/research/issues/{issue.Id}", update)).Content.ReadFromJsonAsync<McpMutationResult>())!.Replayed);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/research/issues/{issue.Id}", update with { RequestId = Guid.NewGuid() })).StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        Assert.Empty(await db.ShopProductPrices.ToArrayAsync()); Assert.Empty(await db.ShopProductPriceHistory.ToArrayAsync());
        Assert.Empty(await db.ExtensionTasks.ToArrayAsync()); Assert.Empty(await db.ColesExtensionTasks.ToArrayAsync());
        Assert.Equal(MatchType.UserConfirmed, (await db.ShopProducts.SingleAsync()).MatchType);
        Assert.Null((await db.ShopProducts.SingleAsync()).LastCheckedDate);
        var savedProduct = await db.Products.SingleAsync();
        savedProduct.GTIN = "4006381333931";
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var conflict = Submission();
        conflict = conflict with { Candidates = [conflict.Candidates[0] with { Identity = conflict.Candidates[0].Identity with { Brand = "Different brand", GTIN = "4006381333931" } }] };
        var conflictingJob = await Post<McpMutationResult>(owner, "/api/research", conflict);
        var conflictingCandidate = (await owner.GetFromJsonAsync<ResearchReview>($"/api/research/{conflictingJob.ResearchJobId}"))!.Candidates.Single();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/research/select", new ResearchSelection(Guid.NewGuid(), conflictingCandidate.Id, first.ShoppingListId.Value, 1))).StatusCode);
        Assert.Equal("Kleenex", (await db.Products.AsNoTracking().SingleAsync()).Brand);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        var tasks = await scope.ServiceProvider.GetRequiredService<ResearchService>().RefreshTasksAsync(first.Account.Id, first.ShoppingListId.Value, 20, default);
        Assert.Equal("missing_price", Assert.Single(tasks).Reason);
        await tx.CommitAsync();
    }

    [PostgreSqlFact]
    public async Task No_price_results_do_not_create_products_and_missing_browser_header_is_rejected()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var app = new AccountSecurityTests.SecurityFactory(); using var owner = app.Client();
        await Register(owner);
        var request = Submission() with { Candidates = [], Issues = [new("coles", "price_unavailable", "No reliable price found.")] };
        var result = await Post<McpMutationResult>(owner, "/api/research", request);
        Assert.Equal("Failed", (await owner.GetFromJsonAsync<ResearchReview>($"/api/research/{result.ResearchJobId}"))!.Status);
        owner.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/research", Submission())).StatusCode);
        owner.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/research/generic/1", new { name = (string?)null, quantity = 1 })).StatusCode);
        using var scope = app.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>().Products.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Migration_preserves_old_rows_enforces_owner_and_evidence_integrity_and_refuses_populated_rollback()
    {
        var original = Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!;
        var schema = "codex_research_migration_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(original); await connection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection)) await create.ExecuteNonQueryAsync();
        try
        {
            await using var db = new MyShoppingListDbContext(new DbContextOptionsBuilder<MyShoppingListDbContext>()
                .UseNpgsql(new NpgsqlConnectionStringBuilder(original) { SearchPath = schema, Pooling = false }.ConnectionString).Options);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261107000000_AddPersonalRetailerComparisons");
            var account = new UserAccount { DisplayName = "Preserved fixture", Email = "migration@example.test", PasswordHash = "fixture", PasswordSalt = "fixture",
                CreatedDate = DateTime.UtcNow, UpdatedDate = DateTime.UtcNow };
            db.Add(account); await db.SaveChangesAsync();
            await db.Database.MigrateAsync();
            Assert.Equal("Preserved fixture", (await db.UserAccounts.SingleAsync()).DisplayName);
            var job = new ResearchJob { Id = Guid.NewGuid(), UserAccountId = account.Id, Query = "Retain evidence", WorkflowVersion = ResearchWorkflow.Version, CreatedAt = DateTime.UtcNow };
            db.Add(job); await db.SaveChangesAsync();
            var candidate = new ResearchCandidate { Id = Guid.NewGuid(), UserAccountId = account.Id, ResearchJobId = job.Id, Key = "fixture" };
            db.Add(candidate); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "ResearchCandidates" SET "ReportJson" = 'changed' WHERE "Id" = {candidate.Id}"""));
            db.Add(new ResearchIssue { Id = Guid.NewGuid(), UserAccountId = account.Id + 1, ResearchJobId = job.Id, Code = "foreign", Retailer = "coles" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
            var failure = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20261107000000_AddPersonalRetailerComparisons"));
            Assert.Contains("Cannot remove populated research", failure.MessageText);
            Assert.Single(await db.ResearchJobs.ToArrayAsync());
        }
        finally { await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", connection); await drop.ExecuteNonQueryAsync(); }
    }

    private static async Task<CurrentSessionResponse> Register(HttpClient client) => await Post<CurrentSessionResponse>(client, "/api/auth/register",
        new RegisterRequest(Guid.NewGuid() + "@example.test", "Research-Fixture-Pass9!", "Research fixture"));

    [PostgreSqlFact]
    public async Task Legacy_conversion_preserves_private_research_and_never_uses_it_as_shared_ownership()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        var product = new Product { UserAccountId = f.A.Id, Name = "Private research fixture", CreatedDate = f.Past, UpdatedDate = f.Past };
        f.Db.Add(product); await f.Db.SaveChangesAsync();
        var job = new ResearchJob { Id = Guid.NewGuid(), UserAccountId = f.A.Id, Query = "Preserve this private research", WorkflowVersion = ResearchWorkflow.Version, CreatedAt = f.Past };
        f.Db.Add(job); await f.Db.SaveChangesAsync();
        var candidate = new ResearchCandidate { Id = Guid.NewGuid(), ResearchJobId = job.Id, UserAccountId = f.A.Id,
            Key = "private", ReportJson = "{\"fixture\":true}", SelectedProductId = product.Id };
        f.Db.Add(candidate); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var plan = await f.Service.PreviewAsync(); Assert.Empty(plan.Conflicts);
        await f.Service.RehearseAsync(plan.PlanHash);
        var after = await f.Db.ResearchCandidates.AsNoTracking().SingleAsync();
        Assert.Equal(candidate.ReportJson, after.ReportJson); Assert.Equal(product.Id, after.SelectedProductId);
        Assert.Equal(job.Query, (await f.Db.ResearchJobs.AsNoTracking().SingleAsync()).Query);
        Assert.Equal("Private research fixture", (await f.Db.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id)).Name);
        await Assert.ThrowsAsync<PostgresException>(() => f.Db.ResearchCandidates.ExecuteUpdateAsync(s => s.SetProperty(c => c.SelectedProductId, f.Source.Id)));
        await Assert.ThrowsAsync<PostgresException>(() => f.Db.ResearchCandidates.ExecuteUpdateAsync(s => s.SetProperty(c => c.SelectedProductId,
            f.Db.Products.Where(p => p.UserAccountId == f.B.Id).Select(p => (long?)p.Id).First())));
    }
    private static async Task<T> Post<T>(HttpClient client, string path, object value)
    {
        var response = await client.PostAsJsonAsync(path, value);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
