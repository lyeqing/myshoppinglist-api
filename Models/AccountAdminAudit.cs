namespace myshoppinglist_api.Models;

public sealed class AccountAdminAudit
{
    public long Id { get; set; }
    public long AdministratorId { get; set; }
    public long AccountId { get; set; }
    public string BeforeJson { get; set; } = "";
    public string AfterJson { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
