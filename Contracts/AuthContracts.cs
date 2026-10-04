namespace myshoppinglist_api.Contracts;

public sealed record AccountResponse(long Id, string DisplayName, bool IsTrial, DateTime? ExpiresDate);
public sealed record CurrentSessionResponse(AccountResponse Account, long? ShoppingListId, DateTime SessionExpiresDate);
public sealed record TrialStartResponse(AccountResponse Account, long ShoppingListId, DateTime SessionExpiresDate, bool Reused);
public sealed record RegisterRequest(string? Email, string? Password, string? DisplayName);
public sealed record SignInRequest(string? Email, string? Password);

public sealed record SessionDevice(string? Platform, string? DeviceType, string? DeviceModel,
    string? OsVersion, string? AppVersion, string? UserAgent, decimal? Latitude = null,
    decimal? Longitude = null, decimal? LocationAccuracy = null, DateTime? LocationCapturedDate = null)
{
    public bool IsValid() => Platform is "android" or "ios"
        && DeviceType is "phone" or "tablet" or "desktop" or "tv" or "unknown"
        && (DeviceModel?.Length ?? 0) <= 200 && (OsVersion?.Length ?? 0) <= 100
        && (AppVersion?.Length ?? 0) <= 100 && (UserAgent?.Length ?? 0) <= 512
        && (Latitude is null || Latitude is >= -90 and <= 90)
        && (Longitude is null || Longitude is >= -180 and <= 180)
        && Latitude.HasValue == Longitude.HasValue
        && (LocationAccuracy is null || LocationAccuracy is >= 0 and <= 100000)
        && (Latitude.HasValue || LocationAccuracy is null && LocationCapturedDate is null)
        && (LocationCapturedDate is null || LocationCapturedDate.Value.Kind == DateTimeKind.Utc);
}
public sealed record MobileSignInRequest(string? Email, string? Password, SessionDevice? Device);
public sealed record MobileRegisterRequest(string? Email, string? Password, string? DisplayName, SessionDevice? Device);
public sealed record MobileSessionResponse(AccountResponse Account, long? ShoppingListId, DateTime SessionExpiresDate, string Token);
