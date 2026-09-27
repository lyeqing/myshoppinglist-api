namespace myshoppinglist_api.Services;

public sealed class CatalogueFreshnessService
{
    private static readonly TimeZoneInfo Adelaide = TimeZoneInfo.FindSystemTimeZoneById("Australia/Adelaide");
    public DateTime Cutoff(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Adelaide).Date;
        var days = ((int)local.DayOfWeek - (int)DayOfWeek.Wednesday + 7) % 7;
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.AddDays(-days), DateTimeKind.Unspecified), Adelaide);
    }
    public bool IsFresh(DateTime? checkedAt, DateTimeOffset now) => checkedAt is { } value
        && value >= Cutoff(now) && value <= now.UtcDateTime;
}
