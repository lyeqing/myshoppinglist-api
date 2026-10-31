using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using myshoppinglist_api.Mcp;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class McpAccountReadTests
{
    [PostgreSqlFact]
    public async Task All_reads_are_owned_explicit_paginated_price_honest_and_database_enforced_read_only()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var first = await ListFixture.CreateAsync();
        await using var second = await ListFixture.CreateAsync();
        await ShoppingListServiceTests.RegisterFixtureAsync(first, false);
        await ShoppingListServiceTests.RegisterFixtureAsync(second, false);
        await first.AddItemAsync(); // A saved generic product without any observation.
        var db = first.Scope.Db;
        var observation = await db.ShopProductPrices.SingleAsync(p => p.ShopProduct.ShopProductCode == first.Scope.Code);
        observation.SourceUrl += "?token=private-secret#private-fragment";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var selection = new CurrentListService(db, first.Scope.Clock);
        var context = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        void Identity(string scopes) => context.HttpContext.User = new(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, first.Scope.UserId.ToString(CultureInfo.InvariantCulture)), new Claim("scope", scopes)], "ValidatedTest"));
        Identity("lists.read products.read");
        var tool = new AccountReadTools(first.Service, selection, new(db, first.Scope.Clock), new(db, first.Scope.Clock), context);
        Assert.Equal("selection_required", (await tool.GetCurrentListAsync(default)).Status);
        await selection.SetAsync(first.Scope.UserId, new(first.Scope.UserId, first.Scope.ListId, 0), default);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET default_transaction_read_only = on");
        try
        {
            Assert.Equal("selected", (await tool.GetCurrentListAsync(default)).Status);
            var firstPage = await tool.GetListItemsAsync(first.Scope.ListId.ToString(), pageSize: 1);
            var generic = Assert.Single(firstPage.Items);
            Assert.Empty(generic.SavedPrices);
            Assert.NotNull(firstPage.NextBeforeId);
            var next = await tool.GetListItemsAsync(first.Scope.ListId.ToString(), firstPage.NextBeforeId, 1);
            var captured = Assert.Single(next.Items);
            Assert.Equal(2, captured.Quantity);
            Assert.Equal("packages", captured.QuantityUnit);
            var price = Assert.Single(captured.SavedPrices);
            Assert.Equal(23, price.Price);
            Assert.Contains("saved_observation", price.Status);
            Assert.Equal("Unknown", price.PriceScope);
            Assert.Null(price.Location);
            Assert.DoesNotContain("private-secret", JsonSerializer.Serialize(next));
            Assert.DoesNotContain("private-fragment", JsonSerializer.Serialize(next));
            Assert.Equal("not_found", (await Assert.ThrowsAsync<McpException>(() => tool.GetListItemsAsync(second.Scope.ListId.ToString()))).Message);
            var search = await tool.SearchMyProductsAsync();
            Assert.Equal(2, search.Total);
            Assert.DoesNotContain(search.Items, p => p.Product.Name.Contains(second.Scope.Code));
            Identity("products.read");
            Assert.Equal("insufficient_scope", (await Assert.ThrowsAsync<McpException>(() => tool.GetCurrentListAsync(default))).Message);
            Identity("lists.read");
            Assert.Equal("insufficient_scope", (await Assert.ThrowsAsync<McpException>(() => tool.SearchMyProductsAsync())).Message);
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("SET default_transaction_read_only = off");
            await db.Database.CloseConnectionAsync();
            context.HttpContext = null;
        }
    }
}
