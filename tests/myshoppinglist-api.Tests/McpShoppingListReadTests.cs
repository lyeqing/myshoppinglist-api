using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Mcp;
using myshoppinglist_api.Models;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class McpShoppingListReadTests
{
    [Fact]
    public async Task Application_does_not_expose_an_mcp_endpoint()
    {
        await using var app = new NoExposureFactory();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new("https://localhost"), HandleCookies = false, AllowAutoRedirect = false
        });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync("/mcp")).StatusCode);
        using var request = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.PostAsync("/mcp", request)).StatusCode);
        Assert.Equal(0, app.Guard.Attempts);
    }

    [Fact]
    public void Sdk_metadata_exposes_only_a_read_only_tool_with_no_identity_arguments()
    {
        using var scope = new DeniedScope(Principal("1", "lists.read"));
        var tool = McpServerTool.Create(new Func<CancellationToken, Task<McpListReadResult>>(scope.Tool.GetMyListsAsync)).ProtocolTool;
        Assert.Equal("get_my_lists", tool.Name);
        Assert.True(tool.Annotations?.ReadOnlyHint);
        Assert.False(tool.Annotations?.DestructiveHint);
        Assert.True(tool.Annotations?.IdempotentHint);
        Assert.False(tool.Annotations?.OpenWorldHint);
        Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
        if (tool.InputSchema.TryGetProperty("properties", out var properties)) Assert.Empty(properties.EnumerateObject());
        Assert.NotNull(tool.OutputSchema);
    }

    [Fact]
    public async Task Missing_context_requires_authentication_before_database_access()
    {
        using var scope = new DeniedScope(null);
        Assert.Equal("authentication_required", (await Assert.ThrowsAsync<McpException>(() => scope.Tool.GetMyListsAsync(default))).Message);
        Assert.Equal(0, scope.Guard.Attempts);
    }

    [Fact]
    public async Task Claims_on_an_unauthenticated_identity_cannot_authorize_access()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier, "1"), new("scope", "lists.read")]));
        using var scope = new DeniedScope(user);
        Assert.Equal("authentication_required", (await Assert.ThrowsAsync<McpException>(() => scope.Tool.GetMyListsAsync(default))).Message);
        Assert.Equal(0, scope.Guard.Attempts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("products.read")]
    [InlineData("lists.write")]
    [InlineData("lists.read.extra")]
    [InlineData("LISTS.READ")]
    public async Task Read_scope_is_required_as_an_exact_case_sensitive_grant(string? grant)
    {
        using var scope = new DeniedScope(Principal("1", grant));
        Assert.Equal("insufficient_scope", (await Assert.ThrowsAsync<McpException>(() => scope.Tool.GetMyListsAsync(default))).Message);
        Assert.Equal(0, scope.Guard.Attempts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.0")]
    [InlineData(" 1")]
    [InlineData("9223372036854775808")]
    public async Task Invalid_account_claims_are_rejected_before_database_access(string? account)
    {
        using var scope = new DeniedScope(Principal(account, "lists.read"));
        Assert.Equal("invalid_identity", (await Assert.ThrowsAsync<McpException>(() => scope.Tool.GetMyListsAsync(default))).Message);
        Assert.Equal(0, scope.Guard.Attempts);
    }

    [Fact]
    public async Task Ambiguous_identities_or_account_claims_are_rejected()
    {
        var duplicate = Principal("1", "lists.read");
        ((ClaimsIdentity)duplicate.Identity!).AddClaim(new(ClaimTypes.NameIdentifier, "2"));
        using (var scope = new DeniedScope(duplicate))
            Assert.Equal("invalid_identity", (await Assert.ThrowsAsync<McpException>(() => scope.Tool.GetMyListsAsync(default))).Message);
        var multiple = new ClaimsPrincipal(Principal("1", "lists.read").Identities.Concat(Principal("2", "lists.read").Identities));
        using (var scope = new DeniedScope(multiple))
            Assert.Equal("invalid_identity", (await Assert.ThrowsAsync<McpException>(() => scope.Tool.GetMyListsAsync(default))).Message);
    }

    [Fact]
    public async Task Scope_on_an_untrusted_extra_identity_cannot_supplement_the_authenticated_identity()
    {
        var user = Principal("1", null);
        user.AddIdentity(new ClaimsIdentity([new("scope", "lists.read")]));
        using var scope = new DeniedScope(user);
        Assert.Equal("insufficient_scope", (await Assert.ThrowsAsync<McpException>(() => scope.Tool.GetMyListsAsync(default))).Message);
        Assert.Equal(0, scope.Guard.Attempts);
    }

    [PostgreSqlFact]
    public async Task Tool_reads_each_current_principals_lists_without_writes_or_internal_account_fields()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var first = await ListFixture.CreateAsync();
        await using var second = await ListFixture.CreateAsync();
        await ShoppingListServiceTests.RegisterFixtureAsync(first, false);
        await ShoppingListServiceTests.RegisterFixtureAsync(second, false);
        var additional = await first.Service.CreateAsync(first.Scope.UserId, new("Another active list"), "UTC", default);
        var db = first.Scope.Db;
        var now = first.Scope.Clock.Now.UtcDateTime;
        db.ShoppingLists.AddRange(
            new ShoppingList { UserAccountId = first.Scope.UserId, Name = "Archived", IsArchived = true, CreatedDate = now, UpdatedDate = now },
            new ShoppingList { UserAccountId = first.Scope.UserId, Name = "Expired", ExpiresDate = now, CreatedDate = now, UpdatedDate = now });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Principal(first.Scope.UserId.ToString(CultureInfo.InvariantCulture), "products.read  lists.read") } };
        try
        {
            var tool = new ShoppingListReadTools(first.Service, accessor);
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
            var owned = await tool.GetMyListsAsync(default);
            Assert.Equal(1, owned.Version);
            Assert.Equal(new[] { first.Scope.ListId.ToString(CultureInfo.InvariantCulture), additional.Id.ToString(CultureInfo.InvariantCulture) },
                owned.Lists.Select(l => l.Id).ToArray());
            var summary = owned.Lists[0];
            Assert.Equal(first.Scope.ListId.ToString(CultureInfo.InvariantCulture), summary.Id);
            Assert.Equal(first.Scope.Code, summary.Name);
            Assert.Equal(DateTimeKind.Utc, summary.CreatedAt.Kind);
            accessor.HttpContext.User = Principal(second.Scope.UserId.ToString(CultureInfo.InvariantCulture), "lists.read");
            Assert.Equal(second.Scope.ListId.ToString(CultureInfo.InvariantCulture), Assert.Single((await tool.GetMyListsAsync(default)).Lists).Id);
            Assert.Empty(db.ChangeTracker.Entries());
            var serialized = JsonSerializer.Serialize(owned, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var contract = JsonDocument.Parse(serialized);
            Assert.Equal(new[] { "lists", "version" }, contract.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.Equal(new[] { "createdAt", "id", "name", "updatedAt" }, contract.RootElement.GetProperty("lists")[0].EnumerateObject().Select(p => p.Name).Order().ToArray());
            await transaction.CommitAsync();
        }
        finally { accessor.HttpContext = null; }
    }

    [PostgreSqlFact]
    public async Task Empty_account_does_not_create_a_default_list()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var fixture = await ListFixture.CreateAsync();
        await ShoppingListServiceTests.RegisterFixtureAsync(fixture, false);
        await fixture.Scope.Db.ShoppingLists.Where(l => l.UserAccountId == fixture.Scope.UserId).ExecuteDeleteAsync();
        fixture.Scope.Db.ChangeTracker.Clear();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Principal(fixture.Scope.UserId.ToString(CultureInfo.InvariantCulture), "lists.read") } };
        try
        {
            await using var transaction = await fixture.Scope.Db.Database.BeginTransactionAsync();
            await fixture.Scope.Db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
            Assert.Empty((await new ShoppingListReadTools(fixture.Service, accessor).GetMyListsAsync(default)).Lists);
            Assert.Equal(0, await fixture.Scope.Db.ShoppingLists.CountAsync(l => l.UserAccountId == fixture.Scope.UserId));
            await transaction.CommitAsync();
        }
        finally { accessor.HttpContext = null; }
    }

    [PostgreSqlFact]
    public async Task Existing_trial_and_inactive_account_rules_remain_enforced()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var fixture = await ListFixture.CreateAsync();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Principal(fixture.Scope.UserId.ToString(CultureInfo.InvariantCulture), "lists.read") } };
        try
        {
            var tool = new ShoppingListReadTools(fixture.Service, accessor);
            Assert.Equal("account_not_supported", (await Assert.ThrowsAsync<McpException>(() => tool.GetMyListsAsync(default))).Message);
            await ShoppingListServiceTests.RegisterFixtureAsync(fixture, false);
            await fixture.Scope.Db.UserAccounts.Where(a => a.Id == fixture.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false));
            Assert.Equal("account_unavailable", (await Assert.ThrowsAsync<McpException>(() => tool.GetMyListsAsync(default))).Message);
        }
        finally { accessor.HttpContext = null; }
    }

    private static ClaimsPrincipal Principal(string? account, string? scope)
    {
        var identity = new ClaimsIdentity("TestValidatedAccessToken");
        if (account is not null) identity.AddClaim(new(ClaimTypes.NameIdentifier, account));
        if (scope is not null) identity.AddClaim(new("scope", scope));
        return new(identity);
    }

    private sealed class NoDatabaseAccess : DbConnectionInterceptor
    {
        public int Attempts { get; private set; }
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result)
        {
            Attempts++;
            throw new InvalidOperationException("Rejected MCP calls must not access the database.");
        }
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("Rejected MCP calls must not access the database.");
        }
    }

    private sealed class NoExposureFactory : WebApplicationFactory<Program>
    {
        public NoDatabaseAccess Guard { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.PostConfigure<ProductImportOptions>(o => o.Enabled = false);
                services.PostConfigure<PriceRefreshOptions>(o => o.Enabled = false);
                services.PostConfigure<UserExtensionOptions>(o => o.PersonalCapturesEnabled = true);
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>();
                services.AddDbContext<MyShoppingListDbContext>(o => o
                    .UseNpgsql("Host=127.0.0.1;Port=1;Database=mcp_never_connect;Timeout=1").AddInterceptors(Guard));
            });
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await Serilog.Log.CloseAndFlushAsync();
        }
    }

    private sealed class DeniedScope : IDisposable
    {
        private readonly MyShoppingListDbContext db;
        private readonly HttpContextAccessor accessor;
        public NoDatabaseAccess Guard { get; } = new();
        public ShoppingListReadTools Tool { get; }
        public DeniedScope(ClaimsPrincipal? principal)
        {
            db = new(new DbContextOptionsBuilder<MyShoppingListDbContext>()
                .UseNpgsql("Host=127.0.0.1;Port=1;Database=mcp_never_connect;Timeout=1").AddInterceptors(Guard).Options);
            accessor = new() { HttpContext = principal is null ? null : new DefaultHttpContext { User = principal } };
            Tool = new(new ShoppingListService(db, TimeProvider.System), accessor);
        }
        public void Dispose() { accessor.HttpContext = null; db.Dispose(); }
    }
}
