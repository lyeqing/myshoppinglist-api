using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Tests;

public class RetailerHttpClientTests
{
    [Theory]
    [InlineData("http://www.coles.com.au/product/test-123")]
    [InlineData("https://127.0.0.1/product/test-123")]
    [InlineData("https://www.woolworths.com.au/product/test-123")]
    public async Task Unsafe_initial_urls_never_reach_http(string url)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Html()));
        var result = await Client(handler).GetPageAsync(new(url), "coles", CancellationToken.None);
        Assert.IsType<ProviderResult<RetailerPage>.Failure>(result);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("https://localhost/private")]
    [InlineData("http://www.coles.com.au/product/test-123")]
    [InlineData("https://www.coles.com.au.evil.example/private")]
    [InlineData("https://www.woolworths.com.au/product/test-123")]
    public async Task Redirects_to_unsafe_or_other_retailer_hosts_are_not_sent(string location)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Redirect(location)));
        var result = await Client(handler).GetPageAsync(ColesProductParserTests.Url, "coles", CancellationToken.None);
        Assert.IsType<ProviderResult<RetailerPage>.Failure>(result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Relative_redirect_is_revalidated_and_returns_final_url()
    {
        var count = 0;
        var handler = new RecordingHandler((_, _) => Task.FromResult(count++ == 0 ? Redirect("/product/canonical-1849307") : Html()));
        var result = Assert.IsType<ProviderResult<RetailerPage>.Success>(await Client(handler).GetPageAsync(
            ColesProductParserTests.Url, "coles", CancellationToken.None));
        Assert.EndsWith("/product/canonical-1849307", result.Value.Url.AbsoluteUri);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Redirect_loop_is_bounded()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Redirect("/product/test-1849307")));
        Assert.IsType<ProviderResult<RetailerPage>.Failure>(await Client(handler, new() { MaximumRedirects = 1 })
            .GetPageAsync(ColesProductParserTests.Url, "coles", CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(403, ProviderFailureKind.AccessRestricted)]
    [InlineData(404, ProviderFailureKind.NotFound)]
    [InlineData(410, ProviderFailureKind.NotFound)]
    [InlineData(429, ProviderFailureKind.RateLimited)]
    [InlineData(408, ProviderFailureKind.Timeout)]
    [InlineData(503, ProviderFailureKind.RemoteServerError)]
    public async Task Http_errors_have_controlled_meaning(int status, ProviderFailureKind kind)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        var result = Assert.IsType<ProviderResult<RetailerPage>.Failure>(await Client(handler).GetPageAsync(
            ColesProductParserTests.Url, "coles", CancellationToken.None));
        Assert.Equal(kind, result.Error.Kind);
    }

    [Fact]
    public async Task Retry_after_is_preserved()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        var result = Assert.IsType<ProviderResult<RetailerPage>.Failure>(await Client(new RecordingHandler((_, _) => Task.FromResult(response)))
            .GetPageAsync(ColesProductParserTests.Url, "coles", CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(30), result.Error.RetryAfter);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Response_limit_applies_with_and_without_content_length(bool knownLength)
    {
        HttpContent content = knownLength ? new StringContent(new string('x', 2048))
            : new StreamContent(new NonSeekableStream(new byte[2048]));
        content.Headers.ContentType = new("text/html");
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        var result = Assert.IsType<ProviderResult<RetailerPage>.Failure>(await Client(handler, new() { MaximumResponseBytes = 1024 })
            .GetPageAsync(ColesProductParserTests.Url, "coles", CancellationToken.None));
        Assert.Equal("response_too_large", result.Error.Code);
    }

    [Fact]
    public async Task Non_html_response_is_rejected()
    {
        var response = Html(); response.Content.Headers.ContentType = new("application/json");
        var result = Assert.IsType<ProviderResult<RetailerPage>.Failure>(await Client(new RecordingHandler((_, _) => Task.FromResult(response)))
            .GetPageAsync(ColesProductParserTests.Url, "coles", CancellationToken.None));
        Assert.Equal("unexpected_content", result.Error.Code);
    }

    [Fact]
    public async Task Timeout_is_distinct_from_caller_cancellation()
    {
        var handler = new RecordingHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Html(); });
        var result = Assert.IsType<ProviderResult<RetailerPage>.Failure>(await Client(handler, new() { TimeoutSeconds = 1 })
            .GetPageAsync(ColesProductParserTests.Url, "coles", CancellationToken.None));
        Assert.Equal(ProviderFailureKind.Timeout, result.Error.Kind);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler)
            .GetPageAsync(ColesProductParserTests.Url, "coles", new CancellationToken(true)));
    }

    [Fact]
    public async Task Timeout_covers_body_reads_after_headers_arrive()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) };
        response.Content.Headers.ContentType = new("text/html");
        var handler = new RecordingHandler((_, _) => Task.FromResult(response));
        var result = Assert.IsType<ProviderResult<RetailerPage>.Failure>(await Client(handler, new() { TimeoutSeconds = 1 })
            .GetPageAsync(ColesProductParserTests.Url, "coles", CancellationToken.None));
        Assert.Equal(ProviderFailureKind.Timeout, result.Error.Kind);
    }

    [Fact]
    public async Task Cancellation_during_request_propagates_to_caller()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Html();
        });
        using var cancellation = new CancellationTokenSource();
        var task = Client(handler).GetPageAsync(ColesProductParserTests.Url, "coles", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Unsupported_encoding_returns_controlled_failure()
    {
        var response = Html(); response.Content.Headers.ContentType!.CharSet = "not-a-real-encoding";
        var result = Assert.IsType<ProviderResult<RetailerPage>.Failure>(await Client(new RecordingHandler((_, _) => Task.FromResult(response)))
            .GetPageAsync(ColesProductParserTests.Url, "coles", CancellationToken.None));
        Assert.Equal("invalid_encoding", result.Error.Code);
    }

    [Fact]
    public async Task Private_dns_answer_prevents_all_connections_even_with_public_answer()
    {
        var connections = 0;
        var connection = new SafeRetailerConnection(new(), (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Loopback }),
            (_, _) => { connections++; return ValueTask.FromResult<Stream>(new MemoryStream()); });
        await Assert.ThrowsAsync<UnsafeRetailerDestinationException>(() => connection.ConnectAsync(new("www.coles.com.au", 443), CancellationToken.None).AsTask());
        Assert.Equal(0, connections);
    }

    [Fact]
    public async Task Empty_dns_answer_is_rejected()
    {
        var connection = new SafeRetailerConnection(new(), (_, _) => Task.FromResult(Array.Empty<IPAddress>()),
            (_, _) => throw new InvalidOperationException("Must not connect."));
        await Assert.ThrowsAsync<UnsafeRetailerDestinationException>(() => connection.ConnectAsync(new("www.coles.com.au", 443), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Connection_uses_checked_numeric_address_without_second_dns_lookup()
    {
        var lookups = 0;
        var connection = new SafeRetailerConnection(new(), (_, _) =>
        {
            lookups++; return Task.FromResult(new[] { IPAddress.Parse(lookups == 1 ? "8.8.8.8" : "127.0.0.1") });
        }, (endpoint, _) =>
        {
            Assert.Equal(IPAddress.Parse("8.8.8.8"), endpoint.Address);
            Assert.Equal(443, endpoint.Port);
            return ValueTask.FromResult<Stream>(new MemoryStream());
        });
        using var stream = await connection.ConnectAsync(new("www.coles.com.au", 443), CancellationToken.None);
        Assert.Equal(1, lookups);
    }

    [Fact]
    public async Task Unsafe_destination_wrapped_by_http_is_not_retryable()
    {
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("Connection failed", new UnsafeRetailerDestinationException()));
        var result = Assert.IsType<ProviderResult<RetailerPage>.Failure>(await Client(handler)
            .GetPageAsync(ColesProductParserTests.Url, "coles", CancellationToken.None));
        Assert.Equal("unsafe_destination", result.Error.Code);
        Assert.False(result.Error.IsRetryable);
    }

    internal static RetailerHttpClient Client(HttpMessageHandler handler, RetailerHttpOptions? options = null) => new(
        new TestHttpClientFactory(handler), new(new RetailerCatalog()), Options.Create(options ?? new()),
        TimeProvider.System, NullLogger<RetailerHttpClient>.Instance);
    internal static HttpResponseMessage Html(string html = "<html></html>") => new(HttpStatusCode.OK)
    { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") };
    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new(location, UriKind.RelativeOrAbsolute);
        return response;
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
    private sealed class StalledStream : MemoryStream
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}

internal sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
}

internal sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return respond(request, cancellationToken);
    }
}
