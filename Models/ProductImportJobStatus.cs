namespace myshoppinglist_api.Models;

public enum ProductImportJobStatus
{
    Queued = 0,
    Processing = 1,
    Completed = 2,
    Partial = 3,
    Failed = 4,
    Cancelled = 5,
}

