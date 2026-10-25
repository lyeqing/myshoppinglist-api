using System.Text;
using System.Text.Json;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Aldi;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Providers.Woolworths;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Services;

// Parses bounded, submitted evidence only. No retailer transport or queue is used.
public sealed class ExtensionProductEvidenceParser(ProductUrlValidator urls, ColesProductParser coles,
    WoolworthsProductParser woolworths, TimeProvider clock, AldiProductParser? aldi = null)
{
    public async Task<ExtractedShopProduct> ParseAsync(string url, ColesEvidence evidence, CancellationToken ct,
        DateTimeOffset? observedAt = null)
    {
        var valid = urls.Validate(url);
        if (!valid.IsValid) throw new UserImportException(400, "Invalid product URL.");
        var date = observedAt ?? clock.GetUtcNow();
        if (valid.Retailer!.Code == "aldi")
        {
            var result = (aldi ?? new()).Parse(valid.ProductUrl!, evidence, date);
            if (result is ProviderResult<ExtractedShopProduct>.Success accepted) return accepted.Value;
            throw new UserImportException(400, ((ProviderResult<ExtractedShopProduct>.Failure)result).Error.Message);
        }
        if (valid.Retailer.Code is not ("coles" or "woolworths"))
            throw new UserImportException(400, "Use a Coles, Woolworths or ALDI product page.");
        var html = EvidenceHtml(evidence, valid.Retailer.Code);
        if (html is null) throw new UserImportException(400, "Invalid product evidence.");
        var page = new RetailerPage(valid.ProductUrl!, html, date);
        var parsed = valid.Retailer.Code == "coles" ? await coles.ParseAsync(page, ct) : await woolworths.ParseAsync(page, ct);
        var code = valid.Retailer.Code == "coles" ? ColesProductParser.ProductCode(valid.ProductUrl!) : WoolworthsProductParser.ProductCode(valid.ProductUrl!);
        if (parsed is not ProviderResult<ExtractedShopProduct>.Success success || success.Value.ShopProductCode != code)
            throw new UserImportException(400, "The page evidence could not identify this product.");
        return success.Value;
    }

    public static string? EvidenceHtml(ColesEvidence? evidence, string retailer = "coles")
    {
        if (evidence is null || evidence.JsonLd?.Length > 20) return null;
        var size = (long)(evidence.NextProductJson?.Length ?? 0) + (evidence.JsonLd?.Sum(s => (long)(s?.Length ?? 0)) ?? 0);
        if (size > 2000000) return null;
        try
        {
            var html = new StringBuilder();
            if (evidence.NextProductJson is { } next)
            {
                using var doc = JsonDocument.Parse(next, new JsonDocumentOptions { MaxDepth = 64 });
                var state = retailer == "woolworths"
                    ? JsonSerializer.Serialize(new { props = new { pageProps = new { pdDetails = new { Product = doc.RootElement } } } })
                    : JsonSerializer.Serialize(new { props = new { pageProps = new { product = doc.RootElement } } });
                html.Append("<script id='__NEXT_DATA__' type='application/json'>").Append(state.Replace("<", "\\u003c")).Append("</script>");
            }
            foreach (var ld in evidence.JsonLd ?? [])
            {
                if (ld is null) return null;
                using var doc = JsonDocument.Parse(ld, new JsonDocumentOptions { MaxDepth = 64 });
                html.Append("<script type='application/ld+json'>").Append(doc.RootElement.GetRawText().Replace("<", "\\u003c")).Append("</script>");
            }
            return html.ToString();
        }
        catch (JsonException) { return null; }
    }
}
