using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Providers.Http;

// Isolated proof of concept; deliberately not registered with the product importer.
public sealed class ColesProductBrowser(IOptions<RetailerSearchOptions> options,
    TimeProvider clock, ILogger<ColesProductBrowser> logger) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(options.Value.MaximumConcurrentBrowsers);

    public async Task<ProviderResult<RetailerPage>> ReadAsync(Uri url, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!options.Value.Enabled) return Fail(ProviderFailureKind.NotSupported, "product_browser_disabled");
        if (ColesProductParser.ProductCode(url) is null || url.AbsoluteUri.Length > 8192)
            return Fail(ProviderFailureKind.InvalidUrl, "invalid_product_url");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        var elapsed = Stopwatch.StartNew();
        float RemainingMilliseconds() => (float)Math.Max(1, options.Value.TimeoutSeconds * 1000 - elapsed.Elapsed.TotalMilliseconds);
        var entered = false;
        try
        {
            await _slots.WaitAsync(deadline.Token); entered = true;
            await using var guard = new RetailerBrowserNetworkGuard("coles", options.Value, deadline.Token);
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
                    || !RetailerBrowserNetworkGuard.IsAllowedColesProductRequest(url, request.Url, request.Method,
                        request.ResourceType, request.IsNavigationRequest))
                { await route.AbortAsync(); return; }
                await route.ContinueAsync();
            });
            // A routed websocket is mocked unless ConnectToServer is explicitly called.
            await context.RouteWebSocketAsync("**/*", _ => { });
            var page = await context.NewPageAsync();
            var read = ReadPageAsync(page, url, RemainingMilliseconds());
            try
            {
                var result = await read.WaitAsync(deadline.Token);
                if (guard.LimitExceeded || Volatile.Read(ref requestLimit) != 0) return Fail(ProviderFailureKind.ParseError, "product_browser_resource_limit");
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
        catch (OperationCanceledException) { return Fail(ProviderFailureKind.Timeout, "product_browser_timeout"); }
        catch (TimeoutException) { return Fail(ProviderFailureKind.Timeout, "product_browser_timeout"); }
        catch (PlaywrightException)
        {
            token.ThrowIfCancellationRequested();
            logger.LogWarning("Coles product browser unavailable; verify browser installation and retailer availability");
            return Fail(ProviderFailureKind.NetworkError, "product_browser_unavailable");
        }
        finally { if (entered) _slots.Release(); }
    }

    internal async Task<ProviderResult<RetailerPage>> ReadPageAsync(IPage page, Uri url, float timeout)
    {
        var response = await page.GotoAsync(url.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = timeout });
        if (response?.Status is 401 or 403) return Fail(ProviderFailureKind.AccessRestricted, "retailer_access_restricted");
        if (response?.Status == 429) return Fail(ProviderFailureKind.RateLimited, "retailer_rate_limited");
        if (response is { Status: >= 500 }) return Fail(ProviderFailureKind.RemoteServerError, "retailer_server_error");
        if (response is { Status: >= 400 }) return Fail(ProviderFailureKind.NotFound, "product_http_error");
        await page.WaitForFunctionAsync("""
            () => /access denied|request unsuccessful|verify you are human|robot or human|just a moment/i.test((document.body?.innerText || '') + document.title)
              || !!document.querySelector('iframe[src*="_Incapsula_Resource"]')
              || !!document.querySelector('script#__NEXT_DATA__, script[type="application/ld+json"]')
            """, null, new() { Timeout = timeout });
        if (await page.EvaluateAsync<bool>("""
            () => /access denied|request unsuccessful|verify you are human|robot or human|just a moment/i.test((document.body?.innerText || '') + document.title)
              || !!document.querySelector('iframe[src*="_Incapsula_Resource"]')
            """)) return Fail(ProviderFailureKind.AccessRestricted, "retailer_access_restricted");
        if (!RetailerBrowserNetworkGuard.IsAllowedColesProductRequest(url, page.Url, "GET", "document", true))
            return Fail(ProviderFailureKind.InvalidProduct, "product_redirected");
        // Transfer only parser inputs. Bound data inside the browser before crossing the protocol.
        var html = await page.EvaluateAsync<string?>("""
            () => {
              const snapshot = document.implementation.createHTMLDocument(document.title.slice(0, 1000));
              const scripts = document.querySelectorAll('script#__NEXT_DATA__, script[type="application/ld+json"]');
              let size = 0;
              if (scripts.length > 100) return null;
              for (const source of scripts) {
                const text = source.textContent || '';
                size += text.length;
                if (size > 2000000) return null;
                const script = snapshot.createElement('script');
                if (source.id === '__NEXT_DATA__') script.id = source.id;
                script.type = source.type;
                script.textContent = text;
                snapshot.body.append(script);
              }
              const html = snapshot.documentElement.outerHTML;
              return html.length <= 2100000 ? html : null;
            }
            """);
        if (html is null) return Fail(ProviderFailureKind.ParseError, "product_snapshot_too_large");
        return new ProviderResult<RetailerPage>.Success(new(new Uri(page.Url), html, clock.GetUtcNow()));
    }

    private static ProviderResult<RetailerPage> Fail(ProviderFailureKind kind, string code) =>
        new ProviderResult<RetailerPage>.Failure(new(kind, code, "The Coles product page could not be read."));
    public void Dispose() => _slots.Dispose();
}
