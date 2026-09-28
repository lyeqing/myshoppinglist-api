namespace myshoppinglist_api.Models;

public sealed class UserExtensionImportTask
{
    public long Id { get; set; }
    public long ProductImportJobId { get; set; }
    public Guid RequestId { get; set; }
    public ProductImportJob ProductImportJob { get; set; } = null!;
    public string Stage { get; set; } = "source";
    public string Status { get; set; } = "Waiting";
    public Guid StepToken { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = "";
    public string? Query { get; set; }
    public string LinksJson { get; set; } = "[]";
    public string MatchesJson { get; set; } = "[]";
    public bool HadFailures { get; set; }
    public string? ErrorCode { get; set; }
    public DateTime UpdatedDate { get; set; }
}
