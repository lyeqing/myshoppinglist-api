using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class PersonalProductRefreshTests
{
    private const string Path = "/api/user-extension/price-refresh";

    [PostgreSqlFact]
    public async Task Refresh_updates_private_prices_without_changing_list_fields_and_replays_safely()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        var capture = f.Request();
        (await client.PostAsJsonAsync("/api/user-extension/captures", capture)).EnsureSuccessStatusCode();
        var target = Assert.Single((await client.GetFromJsonAsync<PersonalRefreshTarget[]>($"{Path}/{f.FirstList.Id}"))!);
        await f.Db.ShoppingListProducts.Where(i => i.Id == target.ItemId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Quantity, 7).SetProperty(i => i.Notes, "Keep my edits"));
        var before = await f.Db.ShoppingListProducts.AsNoTracking().SingleAsync(i => i.Id == target.ItemId);
        var request = new PersonalPriceRefreshRequest(f.FirstList.Id, target.ItemId, target.MappingId,
            CaptureApiScope.Evidence(capture.Url, 6m), f.Clock.Now.AddSeconds(-1));
        for (var retry = 0; retry < 2; retry++)
        {
            var result = await client.PostAsJsonAsync(Path, request);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            Assert.True((await result.Content.ReadFromJsonAsync<PersonalPriceRefreshResponse>())!.Updated);
        }
        var after = await f.Db.ShoppingListProducts.AsNoTracking().SingleAsync(i => i.Id == target.ItemId);
        Assert.Equal(before.Quantity, after.Quantity); Assert.Equal(before.Notes, after.Notes);
        Assert.Equal(before.IsPurchased, after.IsPurchased); Assert.Equal(before.IsHidden, after.IsHidden);
        Assert.Equal(before.UpdatedDate, after.UpdatedDate);
        Assert.Equal(6m, (await f.Db.ShopProductPrices.AsNoTracking().SingleAsync()).Price);
        Assert.Equal(2, await f.Db.ContributionObservations.CountAsync());
        Assert.Equal(2, await f.Db.ShopProductPriceHistory.CountAsync());
        Assert.Single(await f.Db.ProductImportJobs.ToArrayAsync());
        Assert.Empty(await f.Db.UserExtensionImportTasks.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Foreign_accounts_missing_items_and_changed_eligibility_cannot_update_prices()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        using var other = await f.ClientAsync(f.Second.Id);
        var capture = f.Request();
        (await client.PostAsJsonAsync("/api/user-extension/captures", capture)).EnsureSuccessStatusCode();
        var target = Assert.Single((await client.GetFromJsonAsync<PersonalRefreshTarget[]>($"{Path}/{f.FirstList.Id}"))!);
        var request = new PersonalPriceRefreshRequest(f.FirstList.Id, target.ItemId, target.MappingId,
            CaptureApiScope.Evidence(capture.Url, 6m), f.Clock.Now.AddSeconds(-1));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Path}/{f.FirstList.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(Path, request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path, request with { ItemId = long.MaxValue })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, request with { ObservedAt = f.Clock.Now.AddMinutes(1) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, request with {
            Evidence = new ColesEvidence(null, [capture.Evidence.JsonLd![0].Replace("123456", "999999")]) })).StatusCode);
        foreach (var purchased in new[] { true, false })
        {
            await f.Db.ShoppingListProducts.Where(i => i.Id == target.ItemId).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.IsPurchased, purchased).SetProperty(i => i.IsHidden, !purchased));
            Assert.Empty((await client.GetFromJsonAsync<PersonalRefreshTarget[]>($"{Path}/{f.FirstList.Id}"))!);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path, request)).StatusCode);
        }
        Assert.Equal(4m, (await f.Db.ShopProductPrices.AsNoTracking().SingleAsync()).Price);
    }

    [PostgreSqlFact]
    public async Task Missing_price_keeps_previous_observation_and_extension_allowlist_is_required()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        var capture = f.Request();
        (await client.PostAsJsonAsync("/api/user-extension/captures", capture)).EnsureSuccessStatusCode();
        var target = Assert.Single((await client.GetFromJsonAsync<PersonalRefreshTarget[]>($"{Path}/{f.FirstList.Id}"))!);
        var request = new PersonalPriceRefreshRequest(f.FirstList.Id, target.ItemId, target.MappingId,
            CaptureApiScope.Evidence(capture.Url, null), f.Clock.Now.AddSeconds(-1));
        var result = await client.PostAsJsonAsync(Path, request);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.False((await result.Content.ReadFromJsonAsync<PersonalPriceRefreshResponse>())!.Updated);
        Assert.Equal(4m, (await f.Db.ShopProductPrices.AsNoTracking().SingleAsync()).Price);
        client.DefaultRequestHeaders.Remove("X-MyShoppingList-Extension");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Path, request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Path}/{f.FirstList.Id}")).StatusCode);
    }
}
