using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Tests;

internal sealed class SocialBrowserTheoryAttribute : TheoryAttribute
{
    public SocialBrowserTheoryAttribute()
    { if (Environment.GetEnvironmentVariable("MYSHOPPINGLIST_SOCIAL_BROWSER") != "1") Skip = "Opt in with the owned frontend on approved port 3002 and disposable PostgreSQL."; }
}

// Real website/gateway/API/database/cookies; only the provider page and token/user-info replies are controlled fixtures.
[Collection("Import worker database")]
public sealed class SocialAuthenticationBrowserTests
{
    [SocialBrowserTheory]
    [InlineData("google", false)]
    [InlineData("facebook", false)]
    [InlineData("microsoft", false)]
    [InlineData("google", true)]
    [InlineData("facebook", true)]
    [InlineData("microsoft", true)]
    public async Task Real_browser_provider_callback_and_session_cross_the_website_gateway(string provider, bool mobile)
    {
        const string website = "http://127.0.0.1:3002";
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var app = new SocialAuthenticationTests.SocialFactory(website) { Subject = "browser-" + provider, Email = null };
        using var upstream = app.CreateClient(new() { BaseAddress = new(website), AllowAutoRedirect = false, HandleCookies = false });
        upstream.Timeout = TimeSpan.FromSeconds(20);
        await using var proxy = new GatewayTestProxy(upstream);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("MYSHOPPINGLIST_SOCIAL_CHROMIUM"),
            Args = ["--disable-background-networking"]
        });
        await using var context = await browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = mobile ? 390 : 1280, Height = mobile ? 844 : 900 },
            IsMobile = mobile, HasTouch = mobile
        });
        await context.RouteAsync("**/*", async route =>
        {
            var url = new Uri(route.Request.Url);
            if (url.IsLoopback) { await route.ContinueAsync(); return; }
            if (route.Request.IsNavigationRequest && new[] { "accounts.google.com", "www.facebook.com", "login.microsoftonline.com", "login.live.com" }.Contains(url.Host))
            {
                var query = QueryHelpers.ParseQuery(url.Query);
                Assert.Equal(website + "/api/auth/social/callback/" + provider, query["redirect_uri"].ToString());
                await route.FulfillAsync(new() { Status = 302, Headers = new Dictionary<string, string>
                { ["Location"] = query["redirect_uri"] + "?code=fixture&state=" + Uri.EscapeDataString(query["state"]!) } });
                return;
            }
            await route.AbortAsync();
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(website);
        long? trialAccount = null;
        if (mobile)
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Start my shopping list", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByText("Your trial is active", new() { Exact = true })).ToBeVisibleAsync();
            var before = await page.APIRequest.GetAsync(website + "/api/auth/me");
            using var json = System.Text.Json.JsonDocument.Parse(await before.TextAsync());
            trialAccount = json.RootElement.GetProperty("account").GetProperty("id").GetInt64();
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        var name = provider switch { "google" => "Google", "facebook" => "Facebook", _ => "Microsoft" };
        await page.GetByRole(AriaRole.Button, new() { Name = "Continue with " + name, Exact = true }).ClickAsync();
        await page.WaitForURLAsync(website + "/account/social");
        await page.GetByRole(AriaRole.Button, new() { Name = "Continue to MyShoppingList", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync(mobile ? "Your trial is now a permanent account" : "You are signed in");
        if (trialAccount is { } original)
        {
            var after = await page.APIRequest.GetAsync(website + "/api/auth/me");
            using var json = System.Text.Json.JsonDocument.Parse(await after.TextAsync());
            Assert.Equal(original, json.RootElement.GetProperty("account").GetProperty("id").GetInt64());
        }
        var cookies = await context.CookiesAsync();
        var token = cookies.Single(c => c.Name == SessionCookie.Name);
        Assert.True(token.HttpOnly); Assert.True(SessionToken.IsValidFormat(token.Value));
        Assert.DoesNotContain(cookies, c => c.Name.StartsWith("mssl.external", StringComparison.Ordinal));
        await page.GotoAsync(website + "/account/security");
        await Assertions.Expect(page.GetByText(name + " — Connected", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Disconnect " + name, Exact = true })).ToBeDisabledAsync();
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        Assert.False((await page.EvaluateAsync<string>("JSON.stringify({local:{...localStorage},session:{...sessionStorage}})")).Contains(token.Value, StringComparison.Ordinal));
        var evidence = Path.Combine("D:/pra/myshoppinglist-api/.local-dotnet/social-evidence", provider + (mobile ? "-mobile.png" : "-desktop.png"));
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        await page.ScreenshotAsync(new() { Path = evidence, FullPage = true });
    }

    private sealed class GatewayTestProxy : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly HttpClient client;
        private readonly Task worker;
        public GatewayTestProxy(HttpClient client)
        {
            this.client = client;
            listener.Prefixes.Add("http://127.0.0.1:5499/"); listener.Start();
            worker = Pump();
        }
        private async Task Pump()
        {
            try
            {
                while (listener.IsListening)
                {
                    var incoming = await listener.GetContextAsync();
                    try
                    {
                        using var request = new HttpRequestMessage(new(incoming.Request.HttpMethod), incoming.Request.RawUrl);
                        if (incoming.Request.HasEntityBody)
                        { using var body = new MemoryStream(); await incoming.Request.InputStream.CopyToAsync(body); request.Content = new ByteArrayContent(body.ToArray()); }
                        foreach (var name in incoming.Request.Headers.AllKeys.Where(x => x is not null))
                        {
                            if (new[] { "Connection", "Content-Length", "Transfer-Encoding" }.Contains(name!, StringComparer.OrdinalIgnoreCase)) continue;
                            if (!request.Headers.TryAddWithoutValidation(name!, incoming.Request.Headers[name!])) request.Content?.Headers.TryAddWithoutValidation(name!, incoming.Request.Headers[name!]);
                        }
                        using var response = await client.SendAsync(request);
                        incoming.Response.StatusCode = (int)response.StatusCode;
                        foreach (var header in response.Headers.Concat(response.Content.Headers))
                            if (!new[] { "Content-Length", "Transfer-Encoding", "Connection" }.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                                foreach (var value in header.Value) incoming.Response.Headers.Add(header.Key, value);
                        var bytes = await response.Content.ReadAsByteArrayAsync();
                        incoming.Response.ContentLength64 = bytes.Length;
                        await incoming.Response.OutputStream.WriteAsync(bytes);
                    }
                    catch { incoming.Response.StatusCode = 502; }
                    finally { incoming.Response.Close(); }
                }
            }
            catch (HttpListenerException) when (!listener.IsListening) { }
            catch (ObjectDisposedException) when (!listener.IsListening) { }
        }
        public async ValueTask DisposeAsync() { listener.Stop(); await worker.WaitAsync(TimeSpan.FromSeconds(5)); listener.Close(); }
    }
}
