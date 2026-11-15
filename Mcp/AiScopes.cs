namespace myshoppinglist_api.Mcp;

public static class AiScopes
{
    public const string ListsRead = "lists.read";
    public const string ListsWrite = "lists.write";
    public const string ProductsRead = "products.read";
    public const string ExtensionCapture = "extension.capture";
    public const string ResearchRead = "research.read";
    public const string ResearchWrite = "research.write";
    public static readonly string[] All = [ListsRead, ListsWrite, ProductsRead, ExtensionCapture, ResearchRead, ResearchWrite];
}
