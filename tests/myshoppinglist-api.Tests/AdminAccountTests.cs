using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class AdminAccountTests
{
    [PostgreSqlFact]
    public async Task Administration_requires_admin_and_revokes_sessions_with_audited_review()
    {
        await using var db = PersistenceScope.Context();
        var admin = TestAccount(); var user = TestAccount();
        db.AddRange(admin, user); await db.SaveChangesAsync();
        var adminToken = new string('b', 64); var userToken = new string('c', 64);
        db.UserSessions.AddRange(Session(admin.Id, adminToken), Session(user.Id, userToken)); await db.SaveChangesAsync();
        try {
            await using var app = new ContributionTestApp(admin.Id);
            using var client = app.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/accounts")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/accounts")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            client.DefaultRequestHeaders.Add("X-MyShoppingList-Request", "1");
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/accounts")).StatusCode);
            var update = new AdminAccountUpdate(true, true, false, "Repeated invalid submissions reviewed", user.UpdatedDate);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/admin/accounts/{user.Id}", update)).StatusCode);
            db.ChangeTracker.Clear();
            var saved = await db.UserAccounts.SingleAsync(a => a.Id == user.Id);
            Assert.True(saved.IsPaid); Assert.True(saved.ContributionBlocked); Assert.False(saved.IsActive);
            Assert.True(await db.UserSessions.Where(s => s.UserAccountId == user.Id).AllAsync(s => s.RevokedDate != null));
            Assert.Equal(1, await db.AccountAdminAudits.CountAsync(a => a.AccountId == user.Id));
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/admin/accounts/{user.Id}", update)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/admin/accounts/{user.Id}", update with { IsActive = true, ExpectedUpdatedDate = saved.UpdatedDate, Reason = "Restore shopping access" })).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        } finally {
            db.ChangeTracker.Clear(); await db.AccountAdminAudits.Where(a => a.AccountId == user.Id).ExecuteDeleteAsync();
            await db.UserAccounts.Where(a => a.Id == user.Id || a.Id == admin.Id).ExecuteDeleteAsync();
        }
    }
    internal static UserAccount TestAccount() => new() { Email = Guid.NewGuid() + "@example.test", DisplayName = "Admin test",
        PasswordHash = "test", PasswordSalt = "test", CreatedDate = DateTime.UtcNow, UpdatedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
    private static UserSession Session(long account, string token) => new() { UserAccountId = account, TokenHash = SessionToken.Hash(token),
        CreatedDate = DateTime.UtcNow, ExpiresDate = DateTime.UtcNow.AddHours(1) };
}

internal sealed class ContributionTestApp(long admin = -1) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services => {
            services.PostConfigure<AdminOptions>(o => o.AccountIds = [admin]);
            services.PostConfigure<ProductImportOptions>(o => o.Enabled = false);
            services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>();
            services.AddDbContext<MyShoppingListDbContext>(o => o.UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!));
        });
    }
}
