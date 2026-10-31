namespace myshoppinglist_api.Models;

public sealed class McpMutationReceipt
{
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public Guid RequestId { get; set; }
    public string Operation { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string ResultJson { get; set; } = "";
    public DateTime CreatedDate { get; set; }
}
