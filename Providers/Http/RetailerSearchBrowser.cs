using Microsoft.Extensions.Options;
using System.Diagnostics;
using Microsoft.Playwright;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Providers.Models;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("myshoppinglist-api.Tests")]

namespace myshoppinglist_api.Providers.Http;

public interface IRetailerSearchBrowser
{
    Task<ProviderResult<RetailerPage>> ReadAsync(string shopCode, string query, CancellationToken token);
}

public sealed class RetailerSearchBrowser(IOptions<RetailerSearchOptions> options,
    TimeProvider clock, ILogger<RetailerSearchBrowser> logger) : IRetailerSearchBrowser, IDisposable
{
    private readonly SemaphoreSlim _slots = new(options.Value.MaximumConcurrentBrowsers);

    public async Task<ProviderResult<RetailerPage>> ReadAsync(string shopCode, string query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!options.Value.Enabled) return Fail(ProviderFailureKind.NotSupported, "search_disabled");
        if (shopCode is not ("coles" or "woolworths") || string.IsNullOrWhiteSpace(query) || query.Length > 160)
            return Fail(ProviderFailureKind.InvalidProduct, "invalid_search");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        var elapsed = Stopwatch.StartNew();
        float RemainingMilliseconds() => (float)Math.Max(1, options.Value.TimeoutSeconds * 1000 - elapsed.Elapsed.TotalMilliseconds);
        var entered = false;
        try
        {
            await _slots.WaitAsync(deadline.Token); entered = true;
            await using var guard = new RetailerBrowserNetworkGuard(shopCode, options.Value, deadline.Token);
            using var playwright = await Playwright.CreateAsync();
            deadline.Token.ThrowIfCancellationRequested();
            await using var browser = await playwright.Chromium.LaunchAsync(new()
            {
                Headless = true, Channel = options.Value.BrowserChannel, ChromiumSandbox = true,
                Timeout = RemainingMilliseconds(),
                Proxy = new() { Server = guard.ProxyUrl },
                Args = ["--proxy-bypass-list=<-loopback>", "--disable-quic", "--force-webrtc-ip-handling-policy=disable_non_proxied_udp"]
            });
            await using var context = await browser.NewContextAsync(new()
            {
                ServiceWorkers = ServiceWorkerPolicy.Block, AcceptDownloads = false,
                Locale = "en-AU", IgnoreHTTPSErrors = false
            });
            var count = 0; var requestLimit = 0;
            await context.RouteAsync("**/*", async route =>
            {
                var request = route.Request;
                if (Interlocked.Increment(ref count) > options.Value.MaximumRequests)
                { Interlocked.Exchange(ref requestLimit, 1); await route.AbortAsync(); return; }
                if (request.PostDataBuffer is { Length: > 32768 }
                    || !RetailerBrowserNetworkGuard.IsAllowedRequest(shopCode, request.Url, request.Method,
                        request.ResourceType, request.IsNavigationRequest))
                { await route.AbortAsync(); return; }
                await route.ContinueAsync();
            });
            // A routed websocket is mocked unless ConnectToServer is explicitly called.
            await context.RouteWebSocketAsync("**/*", _ => { });
            var page = await context.NewPageAsync();
            var url = ProductSearchQueryBuilder.SearchUrl(shopCode, query);
            var read = ReadPageAsync(page, shopCode, url, RemainingMilliseconds());
            try
            {
                var result = await read.WaitAsync(deadline.Token);
                if (guard.LimitExceeded || Volatile.Read(ref requestLimit) != 0) return Fail(ProviderFailureKind.ParseError, "search_resource_limit");
                return result;
            }
            finally
            {
                // Close cancels pending navigation/evaluation; observe the task before disposing the browser.
                await context.CloseAsync();
                try { await read; } catch (Exception error) when (error is PlaywrightException or OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Fail(ProviderFailureKind.Timeout, "search_timeout"); }
        catch (TimeoutException) { return Fail(ProviderFailureKind.Timeout, "search_timeout"); }
        catch (PlaywrightException)
        {
            token.ThrowIfCancellationRequested();
            logger.LogWarning("Browser search unavailable for {ShopCode}; verify browser installation and retailer availability", shopCode);
            return Fail(ProviderFailureKind.NetworkError, "search_browser_unavailable");
        }
        finally { if (entered) _slots.Release(); }
    }

    internal async Task<ProviderResult<RetailerPage>> ReadPageAsync(IPage page, string shop, Uri url, float timeout)
    {
        var response = await page.GotoAsync(url.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = timeout });
        if (response?.Status is 401 or 403) return Fail(ProviderFailureKind.AccessRestricted, "retailer_access_restricted");
        if (response?.Status == 429) return Fail(ProviderFailureKind.RateLimited, "retailer_rate_limited");
        if (response is { Status: >= 500 }) return Fail(ProviderFailureKind.RemoteServerError, "retailer_server_error");
        if (response is { Status: >= 400 }) return Fail(ProviderFailureKind.ParseError, "search_http_error");
        await page.WaitForFunctionAsync("""
            shop => {
              const text = document.body?.innerText || '';
              const blocked = /access denied|request unsuccessful|verify you are human|robot or human|just a moment/i.test(text + document.title)
                || !!document.querySelector('iframe[src*="_Incapsula_Resource"]');
              const empty = /no results found|no products found|we couldn't find any|we couldn’t find any/i.test(text)
                || (shop === 'coles' && /no results for/i.test(text));
              const results = shop === 'coles'
                ? document.querySelector('.coles-targeting-search-content-container a.product__link[href]')
                : Array.from(document.querySelectorAll('[data-testid="search-results-product-scrollable-content"] wc-product-tile'))
                    .some(tile => tile.shadowRoot?.querySelector('a[href*="/shop/productdetails/"]'));
              return blocked || empty || !!results;
            }
            """, shop, new() { Timeout = timeout });
        if (await page.EvaluateAsync<bool>("""
            () => /access denied|request unsuccessful|verify you are human|robot or human|just a moment/i.test((document.body?.innerText || '') + document.title)
              || !!document.querySelector('iframe[src*="_Incapsula_Resource"]')
            """)) return Fail(ProviderFailureKind.AccessRestricted, "retailer_access_restricted");
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var final) || !RetailerBrowserNetworkGuard.IsSearchPage(shop, final))
            return Fail(ProviderFailureKind.InvalidProduct, "search_redirected");
        var html = await page.EvaluateAsync<string>("""
            shop => {
              const snapshot = document.implementation.createHTMLDocument(document.title);
              const text = snapshot.createElement('p'); text.setAttribute('data-search-visible-text', '');
              text.textContent = (document.body?.innerText || '').slice(0, 50000); snapshot.body.append(text);
              const container = snapshot.createElement('section');
              const selector = shop === 'coles' ? '.coles-targeting-search-content-container' : '[data-testid="search-results-product-scrollable-content"]';
              if (shop === 'coles') container.className = 'coles-targeting-search-content-container';
              else container.setAttribute('data-testid','search-results-product-scrollable-content');
              const source = document.querySelector(selector);
              if (source) {
                const roots = shop === 'coles' ? [source] : Array.from(source.querySelectorAll('wc-product-tile')).map(t=>t.shadowRoot).filter(Boolean);
                for (const root of roots.slice(0,100)) {
                  for (const a of Array.from(root.querySelectorAll('a[href]')).slice(0,200)) {
                    const link = snapshot.createElement('a'); link.setAttribute('href', a.getAttribute('href'));
                    link.textContent = a.textContent || a.getAttribute('aria-label') || '';
                    container.append(link);
                  }
                }
                snapshot.body.append(container);
              }
              return snapshot.documentElement.outerHTML;
            }
            """, shop);
        if (html.Length > 250000) return Fail(ProviderFailureKind.ParseError, "search_snapshot_too_large");
        return new ProviderResult<RetailerPage>.Success(new(final, html, clock.GetUtcNow()));
    }

    private static ProviderResult<RetailerPage> Fail(ProviderFailureKind kind, string code) =>
        new ProviderResult<RetailerPage>.Failure(new(kind, code, "The retailer search could not be completed."));
    public void Dispose() => _slots.Dispose();
}
