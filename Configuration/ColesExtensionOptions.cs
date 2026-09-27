namespace myshoppinglist_api.Configuration;

public sealed class ColesExtensionOptions
{
    public const string SectionName = "ColesExtension";
    // Set via ColesExtension__WorkerKey. Empty disables worker access, not queueing.
    public string WorkerKey { get; set; } = "";
}
