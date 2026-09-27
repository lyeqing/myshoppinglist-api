using System.Text.RegularExpressions;

namespace myshoppinglist_api.Configuration;

public sealed class UserExtensionOptions
{
    public const string SectionName = "UserExtension";
    public string[] AllowedExtensionIds { get; set; } = [];

    public bool Allows(HttpRequest request)
    {
        var ids = request.Headers["X-MyShoppingList-Extension"];
        if (ids.Count != 1 || !Regex.IsMatch(ids.ToString(), "^[a-p]{32}$")
            || !AllowedExtensionIds.Contains(ids.ToString(), StringComparer.Ordinal)) return false;
        var origin = request.Headers.Origin;
        return origin.Count == 0 || origin.Count == 1 && origin.ToString() == $"chrome-extension://{ids}";
    }
}
