using myshoppinglist_api.Services;
namespace myshoppinglist_api.Tests;
public class CatalogueFreshnessTests
{
    [Theory]
    [InlineData("2026-09-22T14:29:59Z", "2026-09-15T14:30:00Z")]
    [InlineData("2026-09-22T14:30:00Z", "2026-09-22T14:30:00Z")]
    [InlineData("2026-10-06T13:30:00Z", "2026-10-06T13:30:00Z")]
    [InlineData("2026-04-07T14:30:00Z", "2026-04-07T14:30:00Z")]
    public void Wednesday_boundary_uses_Adelaide_daylight_saving(string now, string expected)
    {
        var service = new CatalogueFreshnessService(); var time = DateTimeOffset.Parse(now);
        var cutoff = DateTimeOffset.Parse(expected).UtcDateTime;
        Assert.Equal(cutoff, service.Cutoff(time));
        Assert.True(service.IsFresh(cutoff, time));
        Assert.False(service.IsFresh(cutoff.AddTicks(-1), time));
        Assert.False(service.IsFresh(time.UtcDateTime.AddSeconds(1), time));
        Assert.False(service.IsFresh(null, time));
    }
}
