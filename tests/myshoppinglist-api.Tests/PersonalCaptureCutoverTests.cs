using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class PersonalCaptureCutoverTests
{
    [PostgreSqlFact]
    public async Task Personal_mode_disables_all_collection_workers_even_with_legacy_settings_enabled()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        Assert.True(f.Api.Services.GetRequiredService<IOptions<UserExtensionOptions>>().Value.PersonalCapturesEnabled);
        // CaptureFactory deliberately configures both legacy settings true.
        // Product import, pack discovery and brand discovery all use this master option.
        Assert.False(f.Api.Services.GetRequiredService<IOptions<ProductImportOptions>>().Value.Enabled);
        Assert.False(f.Api.Services.GetRequiredService<IOptions<PriceRefreshOptions>>().Value.Enabled);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/shopping-lists")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/shopping-lists/{f.FirstList.Id}/items")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/user-extension/captures", f.Request())).StatusCode);
        Assert.Single(await f.Db.Products.ToArrayAsync());
        Assert.Empty(await f.Db.ColesExtensionTasks.ToArrayAsync());
        Assert.Empty(await f.Db.UserExtensionImportTasks.ToArrayAsync());
        Assert.Empty(await f.Db.PackDiscoveryJobs.ToArrayAsync());
        Assert.Empty(await f.Db.BrandDiscoveryJobs.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Old_clients_receive_no_shared_work_and_cannot_submit_or_schedule_collection()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.Second.Id);
        foreach (var suffix in new[] { "", "/" })
        {
            var preference = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/contributions/preference" + suffix);
            Assert.False(preference.GetProperty("enabled").GetBoolean());
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/contributions/claim" + suffix, new { })).StatusCode);
        }
        foreach (var path in new[] {
            "/api/contributions/1/result", "/api/user-extension/imports/",
            $"/api/shopping-lists/{f.SecondList.Id}/products/url",
            $"/api/shopping-lists/{f.SecondList.Id}/refresh-prices",
            $"/api/shopping-lists/{f.SecondList.Id}/pack-discovery",
            $"/api/shopping-lists/{f.SecondList.Id}/brand-discovery",
            $"/api/shopping-lists/{f.SecondList.Id}/items/1/refresh-prices",
            $"/api/shopping-lists/{f.SecondList.Id}/items/1/pack-discovery",
            $"/api/shopping-lists/{f.SecondList.Id}/items/1/brand-discovery",
            $"/api/shopping-lists/{f.SecondList.Id}/items/1/retry-comparison"
        })
        {
            var response = await client.PostAsJsonAsync(path, new { });
            Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        }
        Assert.Equal(HttpStatusCode.Gone, (await client.PutAsJsonAsync("/api/contributions/preference", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync("/api/user-extension/imports/1")).StatusCode);
        using var worker = f.Api.CreateClient();
        worker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('w', 64));
        Assert.Equal(HttpStatusCode.Gone, (await worker.GetAsync("/api/coles-worker/tasks")).StatusCode);
        foreach (var action in new[] { "claim", "result", "retry" })
            Assert.Equal(HttpStatusCode.Gone, (await worker.PostAsJsonAsync("/api/coles-worker/tasks/1/" + action, new { })).StatusCode);
        Assert.Empty(await f.Db.Products.ToArrayAsync());
        Assert.Empty(await f.Db.ProductImportJobs.ToArrayAsync());
        Assert.Empty(await f.Db.ContributionObservations.ToArrayAsync());
        Assert.Empty(await f.Db.PackDiscoveryJobs.ToArrayAsync());
        Assert.Empty(await f.Db.BrandDiscoveryJobs.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Disabled_personal_mode_keeps_legacy_reads_and_authentication_remains_required()
    {
        await using var f = await CaptureApiScope.CreateAsync(enabled: false);
        using var client = await f.ClientAsync(f.Second.Id);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/contributions/preference")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await client.PostAsJsonAsync("/api/user-extension/captures", f.Request() with { ListId = f.SecondList.Id })).StatusCode);
        using var anonymous = f.Api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/coles-worker/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/contributions/preference")).StatusCode);
        Assert.Empty(await f.Db.ProductImportJobs.ToArrayAsync());
    }
}