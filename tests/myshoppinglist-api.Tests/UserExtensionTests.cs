using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class UserExtensionTests
{
    private const string ExtensionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Extension_login_exception_requires_configured_id_and_does_not_weaken_website_protection()
    {
        var options = new UserExtensionOptions { AllowedExtensionIds = [ExtensionId] };
        foreach (var (path, id, origin, expected) in new[] {
            ("/api/user-extension/login", ExtensionId, "chrome-extension://" + ExtensionId, true),
            ("/api/user-extension/login", ExtensionId, "https://evil.test", false),
            ("/api/user-extension/login", new string('b', 32), "chrome-extension://" + new string('b', 32), false),
            ("/api/auth/login", ExtensionId, "chrome-extension://" + ExtensionId, false) })
        {
            var services = new ServiceCollection().AddLogging().AddOptions().AddSingleton(Options.Create(options)).BuildServiceProvider();
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Method = "POST"; context.Request.Path = path; context.Request.Scheme = "http"; context.Request.Host = new("localhost:5392");
            context.Request.Headers.Origin = origin; context.Request.Headers["X-MyShoppingList-Extension"] = id;
            context.Request.Headers[CookieRequestProtection.HeaderName] = "1";
            var passed = false;
            await new CookieRequestProtection(_ => { passed = true; return Task.CompletedTask; }).InvokeAsync(context);
            Assert.Equal(expected, passed);
        }
        Assert.False(new UserExtensionOptions().Allows(new DefaultHttpContext().Request));
    }

    [PostgreSqlFact]
    public async Task Extension_login_issues_separate_bearer_session_and_ownership_still_applies()
    {
        await using var f = await ListFixture.CreateAsync();
        var password = "TestPassword1!";
        var email = Guid.NewGuid() + "@example.test";
        var hash = PasswordHasher.Hash(password);
        await f.Scope.Db.UserAccounts.Where(u => u.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.IsTrial, false).SetProperty(u => u.Email, email)
            .SetProperty(u => u.PasswordHash, hash.Hash).SetProperty(u => u.PasswordSalt, hash.Salt));
        await using var app = new ExtensionFactory(f);
        using var client = app.CreateClient(new() { BaseAddress = new("http://localhost"), HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-MyShoppingList-Extension", ExtensionId);
        client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        client.DefaultRequestHeaders.Add("Origin", "chrome-extension://" + ExtensionId);
        var login = await client.PostAsJsonAsync("/api/user-extension/login", new SignInRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False(login.Headers.Contains("Set-Cookie"));
        Assert.True(login.Headers.CacheControl!.NoStore);
        var payload = await login.Content.ReadFromJsonAsync<JsonElement>();
        var token = payload.GetProperty("token").GetString()!;
        Assert.True(SessionToken.IsValidFormat(token));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/shopping-lists")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/shopping-lists/9223372036854775807/items")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/shopping-lists/default", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/shopping-lists")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Default_creation_is_idempotent_concurrent_and_owned()
    {
        await using var f = await ListFixture.CreateAsync();
        Assert.Null(await f.Service.EnsureDefaultAsync(-1, default));
        Assert.Equal(f.Scope.ListId, await f.Service.EnsureDefaultAsync(f.Scope.UserId, default));
        await f.Scope.Db.ShoppingLists.Where(l => l.Id == f.Scope.ListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, true));
        async Task<long?> Create()
        {
            await using var db = PersistenceScope.Context();
            return await new ShoppingListService(db, f.Scope.Clock).EnsureDefaultAsync(f.Scope.UserId, default);
        }
        var ids = await Task.WhenAll(Create(), Create());
        Assert.NotNull(ids[0]); Assert.Equal(ids[0], ids[1]); Assert.NotEqual(f.Scope.ListId, ids[0]);
        Assert.Equal(1, await f.Scope.Db.ShoppingLists.CountAsync(l => l.UserAccountId == f.Scope.UserId && !l.IsArchived));
    }

    private sealed class ExtensionFactory(ListFixture fixture) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(fixture.Scope.Clock);
                services.PostConfigure<ProductImportOptions>(o => o.Enabled = false);
                services.PostConfigure<UserExtensionOptions>(o => o.AllowedExtensionIds = [ExtensionId]);
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>();
                services.AddDbContext<MyShoppingListDbContext>(o => o.UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!));
            });
        }
    }
}
