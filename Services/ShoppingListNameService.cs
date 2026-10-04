using System.Globalization;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Data;

namespace myshoppinglist_api.Services;

public static class ShoppingListNameService
{
    public static string Prefix(DateTime utc, string? timezone)
    {
        TimeZoneInfo zone = TimeZoneInfo.Utc;
        if (timezone is { Length: > 0 and <= 100 }) {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(timezone); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return "Shopping_List_" + local.ToString("dd_MM_yyyy", CultureInfo.InvariantCulture) + "_";
    }
    public static string First(DateTime utc, string? timezone) => Prefix(utc, timezone) + "01";

    // Caller holds the account row lock, serialising creation across requests/devices.
    public static async Task<string> NextAsync(MyShoppingListDbContext db, long account, DateTime utc, string? timezone, CancellationToken ct)
    {
        var prefix = Prefix(utc, timezone);
        var names = await db.ShoppingLists.Where(l => l.UserAccountId == account && l.Name.StartsWith(prefix)).Select(l => l.Name).ToListAsync(ct);
        long highest = 0;
        foreach (var name in names)
            if (long.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)) highest = Math.Max(highest, number);
        return prefix + checked(highest + 1).ToString("D2", CultureInfo.InvariantCulture);
    }
}
