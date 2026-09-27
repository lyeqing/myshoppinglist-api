using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using myshoppinglist_api.Configuration;

namespace myshoppinglist_api.Tests;
[Collection("Import worker database")]
public class ColesExtensionEndpointTests
{
    private const string Key = "test-only-coles-worker-secret-1234567890";
    [Theory]
    [InlineData(null)]
    [InlineData("incorrect-worker-key-123456789012345")]
    public async Task Unauthenticated_workers_cannot_read_or_write_queue(string? key)
    {
        await using var app = new Factory(); using var client = app.CreateClient();
        if (key is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/coles-worker/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/coles-worker/tasks/1/claim", new { workerId = "test" })).StatusCode);
    }
    [Fact]
    public async Task Worker_authentication_allows_its_policy_but_not_user_accounts()
    {
        await using var app = new Factory(); using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        // Validation happens after worker authorization without needing database access or browser CSRF headers.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/coles-worker/tasks/1/claim", new { workerId = "invalid worker" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }
    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services => {
                services.PostConfigure<ProductImportOptions>(o => o.Enabled = false);
                services.PostConfigure<ColesExtensionOptions>(o => o.WorkerKey = Key);
            });
        }
    }
}
