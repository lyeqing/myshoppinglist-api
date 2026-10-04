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
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class MobileAuthTests
{
    private static readonly SessionDevice Android = new("android", "phone", "Pixel", "15", "1.0.0", "MyShoppingList test");

    [Fact]
    public void Metadata_allows_optional_location_and_rejects_invalid_values()
    {
        Assert.True(Android.IsValid());
        Assert.True((Android with { Platform = "ios", DeviceType = "tablet", Latitude = -34.9m, Longitude = 138.6m, LocationAccuracy = 20, LocationCapturedDate = DateTime.UtcNow }).IsValid());
        Assert.False((Android with { Latitude = 91, Longitude = 0 }).IsValid());
        Assert.False((Android with { Latitude = 0 }).IsValid());
        Assert.False((Android with { LocationAccuracy = 20 }).IsValid());
        Assert.False((Android with { Platform = "anything" }).IsValid());
        Assert.False((Android with { DeviceModel = new string('a', 201) }).IsValid());
        Assert.False((Android with { Latitude = 0, Longitude = 0, LocationAccuracy = -1 }).IsValid());
    }

    [PostgreSqlFact]
    public async Task Mobile_registration_login_tracking_and_revocation_share_existing_sessions()
    {
        await using var app = new MobileFactory();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        var email = Guid.NewGuid() + "@example.test";
        long? accountId = null;
        try
        {
            var registration = await client.PostAsJsonAsync("/api/auth/mobile/register", new MobileRegisterRequest(email, "Abcdef1!", "Mobile shopper", Android));
            Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
            Assert.True(registration.Headers.CacheControl!.NoStore);
            Assert.False(registration.Headers.Contains("Set-Cookie"));
            var registered = (await registration.Content.ReadFromJsonAsync<MobileSessionResponse>())!;
            accountId = registered.Account.Id;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.Token);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
            client.DefaultRequestHeaders.Authorization = null;
            var location = Android with { Latitude = -34.9m, Longitude = 138.6m, LocationAccuracy = 20, LocationCapturedDate = DateTime.UtcNow };
            var login = await client.PostAsJsonAsync("/api/auth/mobile/login", new MobileSignInRequest(email, "Abcdef1!", location));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var signedIn = (await login.Content.ReadFromJsonAsync<MobileSessionResponse>())!;
            Assert.Equal(accountId, signedIn.Account.Id);
            await using var db = PersistenceScope.Context();
            var sessions = await db.UserSessions.Where(s => s.UserAccountId == accountId).OrderBy(s => s.Id).ToListAsync();
            Assert.Equal(2, sessions.Count);
            Assert.NotNull(sessions[0].RevokedDate);
            Assert.Null(sessions[0].Latitude);
            Assert.Equal("phone", sessions[1].DeviceType);
            Assert.Equal("android", sessions[1].Platform);
            Assert.Equal(-34.9m, sessions[1].Latitude);
            Assert.Equal(SessionToken.Hash(signedIn.Token), sessions[1].TokenHash);
            Assert.NotNull(sessions[1].LastSeenDate);
            var invalid = await client.PostAsJsonAsync("/api/auth/mobile/login", new MobileSignInRequest(email, "Abcdef1!", Android with { Longitude = 181 }));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal(2, await db.UserSessions.CountAsync(s => s.UserAccountId == accountId));
            client.DefaultRequestHeaders.Add("Origin", "https://other.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/auth/mobile/login", new MobileSignInRequest(email, "Abcdef1!", Android))).StatusCode);
        }
        finally { if (accountId.HasValue) { await using var db = PersistenceScope.Context(); await db.UserAccounts.Where(u => u.Id == accountId).ExecuteDeleteAsync(); } }
    }

    private sealed class MobileFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services => {
                services.PostConfigure<ProductImportOptions>(o => o.Enabled = false);
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>();
                services.AddDbContext<MyShoppingListDbContext>(o => o.UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!));
            });
        }
    }
}
