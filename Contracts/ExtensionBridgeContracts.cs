using System.Text.Json.Serialization;

namespace myshoppinglist_api.Contracts;

public sealed record ExtensionDeviceRegistration([property: JsonRequired] long ExpectedAccountId, Guid InstallationId,
    string ExtensionVersion, int ProtocolVersion);
public sealed record ExtensionDeviceCredentials(int Version, Guid DeviceId, string DeviceToken, DateTime LinkedAt);
public sealed record ExtensionDeviceHeartbeat(string ExtensionVersion, string[] SupportedRetailers, bool Interactive, int ProtocolVersion);
public sealed record ExtensionDeviceSummary(Guid Id, string ExtensionVersion, string[] SupportedRetailers, DateTime LinkedAt,
    DateTime? LastSeenAt, DateTime? RevokedAt, bool SessionActive);
public sealed record ExtensionPresence(int Version, string Status, DateTime? LastSeenAt, string[] Retailers, string Instructions);
public sealed record ExtensionTaskInfo(int Version, string Id, string Type, string Status, string Retailer, string Url,
    string? Query, string? ListId, string? ListName, int Quantity, string ClientName, DateTime CreatedAt, DateTime ExpiresAt,
    string? ErrorCode, System.Text.Json.JsonElement? Result = null);
public sealed record ExtensionTaskRequestResult(int Version, string Status, ExtensionTaskInfo? Task, string Instructions);
public sealed record ExtensionTaskClaim(int Version, ExtensionTaskInfo Task, string? ClaimToken);
public sealed record ExtensionTaskDecision(bool Approve);
public sealed record ExtensionTaskClaimRequest(string ClaimToken);
public sealed record ExtensionTaskFailure(string ClaimToken, string ErrorCode);
public sealed record ExtensionTaskCapture(string ClaimToken, ColesEvidence Evidence, DateTimeOffset ObservedAt);
public sealed record ExtensionSearchCandidate(string Url, string? Name);
public sealed record ExtensionTaskSearch(string ClaimToken, ExtensionSearchCandidate[] Candidates, bool EmptyConfirmed, DateTimeOffset ObservedAt);
