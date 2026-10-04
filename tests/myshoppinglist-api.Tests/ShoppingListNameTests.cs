using myshoppinglist_api.Models;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

public class ShoppingListNameTests
{
    [Theory]
    [InlineData("Australia/Adelaide", "Shopping_List_04_10_2026_01")]
    [InlineData("America/Los_Angeles", "Shopping_List_03_10_2026_01")]
    [InlineData("Pacific/Auckland", "Shopping_List_04_10_2026_01")]
    [InlineData("UTC", "Shopping_List_03_10_2026_01")]
    [InlineData("invalid-timezone", "Shopping_List_03_10_2026_01")]
    [InlineData(null, "Shopping_List_03_10_2026_01")]
    public void Uses_customer_local_date_across_midnight(string? zone, string expected) =>
        Assert.Equal(expected, ShoppingListNameService.First(new DateTime(2026, 10, 3, 16, 0, 0, DateTimeKind.Utc), zone));

    [PostgreSqlFact]
    public async Task Sequence_includes_archived_lists_and_skips_gaps_and_other_dates()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var now = new DateTime(2026, 10, 3, 16, 0, 0, DateTimeKind.Utc);
        foreach (var name in new[] { "Shopping_List_04_10_2026_01", "Shopping_List_04_10_2026_03", "Shopping_List_03_10_2026_99", "My old list" })
            scope.Db.ShoppingLists.Add(new ShoppingList { UserAccountId = scope.UserId, Name = name, IsArchived = true, CreatedDate = now, UpdatedDate = now });
        await scope.Db.SaveChangesAsync();
        Assert.Equal("Shopping_List_04_10_2026_04", await ShoppingListNameService.NextAsync(scope.Db, scope.UserId, now, "Australia/Adelaide", default));
        Assert.Equal("Shopping_List_03_10_2026_100", await ShoppingListNameService.NextAsync(scope.Db, scope.UserId, now, "UTC", default));
    }
}
