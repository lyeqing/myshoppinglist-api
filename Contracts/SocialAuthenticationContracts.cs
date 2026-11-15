namespace myshoppinglist_api.Contracts;

public sealed record SocialStartRequest(string Provider, string Purpose, long? ExpectedAccountId = null, bool LeaveTrialSeparate = false);
public sealed record SocialCompleteRequest(bool LeaveTrialSeparate = false);
public sealed record SocialPasswordProof(long ExpectedAccountId, string? Password);
public sealed record SocialDisconnectRequest(long ExpectedAccountId, string Provider);
public sealed record SocialEmailRequest(long ExpectedAccountId, string? Email);
public sealed record SocialPasswordRequest(long ExpectedAccountId, string? Token, string? NewPassword);
public sealed record SocialProviderView(string Provider, bool Available, bool Connected);
public sealed record SocialMethodsView(bool HasPassword, bool CanAddPassword, bool EmailDeliveryAvailable,
    bool RecentlyAuthenticated, SocialProviderView[] Providers);
public sealed record SocialCompletion(string Outcome, CurrentSessionResponse? Session = null);
