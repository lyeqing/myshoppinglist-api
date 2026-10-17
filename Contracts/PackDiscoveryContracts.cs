using System.Text.Json.Serialization;

namespace myshoppinglist_api.Contracts;

public sealed record PackDiscoveryRequest([property: JsonRequired] long ExpectedAccountId);
public sealed record PackDiscoveryProgress(long Id, long ItemId, string ProductName, string ShopName,
    string Status, int FoundCount, string? ErrorCode, DateTime UpdatedDate, DateTime? RetryAfter);
public sealed record PackDiscoveryView(long AccountId, long ListId, long? ItemId, bool Enabled, bool CanStart,
    string? Reason, PackDiscoveryProgress[] Jobs, int Queued = 0, int AlreadyRunning = 0, int Recent = 0);
public sealed record PackDiscoveryClaim(long Id, Guid Token);
