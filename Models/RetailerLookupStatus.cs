namespace myshoppinglist_api.Models;

public enum RetailerLookupStatus
{
    Pending = 0,
    Checking = 1,
    Exact = 2,
    Likely = 3,
    Possible = 4,
    NotFound = 5,
    Unavailable = 6,
    CheckFailed = 7,
    NotSupported = 8,
}

