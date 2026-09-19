using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Providers.Http;

public sealed record RetailerPage(Uri Url, string Html, DateTimeOffset CheckedDate);

public sealed class RetailerHttpClient(
    IHttpClientFactory clients, ProductUrlValidator validator, IOptions<RetailerHttpOptions> options,
    TimeProvider clock, ILogger<RetailerHttpClient> logger)
{
    public const string ClientName = "RetailerPages";

    public async Task<ProviderResult<RetailerPage>> GetPageAsync(Uri url, string shopCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        using var client = clients.CreateClient(ClientName);
        try
        {
            var current = url;
            for (var redirect = 0; ; redirect++)
            {
                var validation = validator.Validate(current.OriginalString);
                if (!validation.IsValid || !string.Equals(validation.Retailer!.Code, shopCode, StringComparison.OrdinalIgnoreCase))
                    return Fail(ProviderFailureKind.InvalidUrl, "unsafe_url", "The retailer URL is not permitted.");
                current = validation.ProductUrl!;
                using var request = new HttpRequestMessage(HttpMethod.Get, current)
                {
                    Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (redirect >= options.Value.MaximumRedirects || response.Headers.Location is not { } location
                        || !Uri.TryCreate(current, location, out var next))
                        return Fail(ProviderFailureKind.InvalidUrl, "invalid_redirect", "The retailer redirect could not be followed safely.");
                    current = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode) return HttpFailure(response);
                if (response.Content.Headers.ContentType?.MediaType is not ("text/html" or "application/xhtml+xml"))
                    return Fail(ProviderFailureKind.ParseError, "unexpected_content", "The retailer did not return an HTML product page.");
                if (response.Content.Headers.ContentLength > options.Value.MaximumResponseBytes)
                    return Fail(ProviderFailureKind.ParseError, "response_too_large", "The retailer page exceeds the download limit.");

                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var body = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (body.Length + read > options.Value.MaximumResponseBytes)
                        return Fail(ProviderFailureKind.ParseError, "response_too_large", "The retailer page exceeds the download limit.");
                    body.Write(buffer, 0, read);
                }
                var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
                var encoding = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
                return new ProviderResult<RetailerPage>.Success(new(current, encoding.GetString(body.ToArray()), clock.GetUtcNow()));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Fail(ProviderFailureKind.Timeout, "retailer_timeout", "The retailer request timed out."); }
        catch (HttpRequestException error)
        {
            if (ContainsUnsafeDestination(error))
                return Fail(ProviderFailureKind.InvalidUrl, "unsafe_destination", "The retailer destination is not permitted.");
            logger.LogWarning("Retailer request failed for {ShopCode}: {ErrorType}", shopCode, error.GetType().Name);
            return Fail(ProviderFailureKind.NetworkError, "retailer_network_error", "The retailer could not be reached.");
        }
        catch (IOException)
        { return Fail(ProviderFailureKind.NetworkError, "retailer_read_error", "The retailer response could not be read."); }
        catch (ArgumentException)
        { return Fail(ProviderFailureKind.ParseError, "invalid_encoding", "The retailer response uses an unsupported encoding."); }
        catch (NotSupportedException)
        { return Fail(ProviderFailureKind.ParseError, "invalid_encoding", "The retailer response uses an unsupported encoding."); }
    }

    private static bool ContainsUnsafeDestination(Exception error) =>
        error is UnsafeRetailerDestinationException || error.InnerException is { } inner && ContainsUnsafeDestination(inner);

    private ProviderResult<RetailerPage> HttpFailure(HttpResponseMessage response)
    {
        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderFailureKind.AccessRestricted,
            HttpStatusCode.NotFound or HttpStatusCode.Gone => ProviderFailureKind.NotFound,
            HttpStatusCode.TooManyRequests => ProviderFailureKind.RateLimited,
            HttpStatusCode.RequestTimeout => ProviderFailureKind.Timeout,
            _ when (int)response.StatusCode >= 500 => ProviderFailureKind.RemoteServerError,
            _ => ProviderFailureKind.InvalidProduct
        };
        var retryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - clock.GetUtcNow() : (TimeSpan?)null);
        return new ProviderResult<RetailerPage>.Failure(new(kind, $"retailer_http_{(int)response.StatusCode}",
            "The retailer could not provide this product page.") { RetryAfter = retryAfter > TimeSpan.Zero ? retryAfter : null });
    }

    private static ProviderResult<RetailerPage> Fail(ProviderFailureKind kind, string code, string message) =>
        new ProviderResult<RetailerPage>.Failure(new(kind, code, message));
}
