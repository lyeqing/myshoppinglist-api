using System.Net;
using System.Net.Sockets;
using System.Text;
using myshoppinglist_api.Providers.Http;

namespace myshoppinglist_api.Tests;

public class RetailerBrowserNetworkGuardTests
{
    [Theory]
    [InlineData("http://www.coles.com.au/search/products?q=test", "GET", "document", true)]
    [InlineData("https://127.0.0.1/search/products?q=test", "GET", "document", true)]
    [InlineData("https://www.coles.com.au.evil.example/script.js", "GET", "script", false)]
    [InlineData("https://www.coles.com.au:444/script.js", "GET", "script", false)]
    [InlineData("https://user:pass@www.coles.com.au/script.js", "GET", "script", false)]
    [InlineData("https://www.coles.com.au/cart", "POST", "fetch", false)]
    [InlineData("https://www.coles.com.au/image.jpg", "GET", "image", false)]
    [InlineData("https://www.coles.com.au/login", "GET", "document", true)]
    public void Unsafe_or_unneeded_requests_are_blocked(string url, string method, string type, bool navigation) =>
        Assert.False(RetailerBrowserNetworkGuard.IsAllowedRequest("coles", url, method, type, navigation));
    [Fact]
    public void Resource_allowlists_are_separate_from_product_and_other_retailer_hosts()
    {
        Assert.True(RetailerBrowserNetworkGuard.IsAllowedRequest("woolworths", "https://cdn0.woolworths.media/script.js", "GET", "script", false));
        Assert.False(RetailerBrowserNetworkGuard.IsAllowedRequest("coles", "https://cdn0.woolworths.media/script.js", "GET", "script", false));
        Assert.False(RetailerBrowserNetworkGuard.IsAllowedRequest("woolworths", "https://www.woolworths.com.au/apis/ui/cart", "POST", "fetch", false));
    }
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task Mixed_public_private_dns_answers_never_connect(string privateAddress)
    {
        var connections = 0;
        await using var guard = new RetailerBrowserNetworkGuard("coles", new(), CancellationToken.None,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse(privateAddress) }),
            (_, _) => { connections++; return ValueTask.FromResult<Stream>(new MemoryStream()); });
        await Assert.ThrowsAsync<UnsafeRetailerDestinationException>(async () => await guard.ConnectPublicAsync("www.coles.com.au", 443, CancellationToken.None));
        Assert.Equal(0, connections);
    }
    [Fact]
    public async Task Numeric_connection_is_pinned_to_the_single_validated_lookup()
    {
        var lookups = 0;
        await using var guard = new RetailerBrowserNetworkGuard("coles", new(), CancellationToken.None,
            (_, _) => { lookups++; return Task.FromResult(new[] { IPAddress.Parse(lookups == 1 ? "8.8.8.8" : "127.0.0.1") }); },
            (endpoint, _) => { Assert.Equal(IPAddress.Parse("8.8.8.8"), endpoint.Address); return ValueTask.FromResult<Stream>(new MemoryStream()); });
        await using var connection = await guard.ConnectPublicAsync("www.coles.com.au", 443, CancellationToken.None);
        Assert.Equal(1, lookups);
        await Assert.ThrowsAsync<UnsafeRetailerDestinationException>(async () => await guard.ConnectPublicAsync("evil.example", 443, CancellationToken.None));
        Assert.Equal(1, lookups);
    }
    [Fact]
    public async Task Socks_proxy_rejects_literal_ip_and_udp_before_any_connection()
    {
        await using var guard = new RetailerBrowserNetworkGuard("coles", new(), CancellationToken.None,
            (_, _) => throw new InvalidOperationException("Must not resolve"));
        foreach (var request in new[] { new byte[] { 5, 1, 0, 1 }, new byte[] { 5, 3, 0, 3 } })
        {
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, new Uri(guard.ProxyUrl).Port);
            var stream = client.GetStream(); await stream.WriteAsync(new byte[] { 5, 1, 0 });
            var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting); Assert.Equal(new byte[] { 5, 0 }, greeting);
            await stream.WriteAsync(request); var reply = new byte[10]; await stream.ReadExactlyAsync(reply);
            Assert.Equal(2, reply[1]);
        }
    }

    [Fact]
    public async Task Transfer_budget_closes_the_tunnel_instead_of_allowing_unbounded_data()
    {
        await using var guard = new RetailerBrowserNetworkGuard("coles", new() { MaximumTransferBytes = 128 }, CancellationToken.None,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
            (_, _) => ValueTask.FromResult<Stream>(new MemoryStream(new byte[1024])));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, new Uri(guard.ProxyUrl).Port, timeout.Token);
        var stream = client.GetStream(); await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
        var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, timeout.Token);
        var host = Encoding.ASCII.GetBytes("www.coles.com.au");
        var request = new byte[] { 5, 1, 0, 3, (byte)host.Length }.Concat(host).Concat(new byte[] { 1, 187 }).ToArray();
        await stream.WriteAsync(request, timeout.Token);
        var reply = new byte[10]; await stream.ReadExactlyAsync(reply, timeout.Token); Assert.Equal(0, reply[1]);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], timeout.Token));
        Assert.True(guard.LimitExceeded);
    }

    [Fact]
    public async Task Disposing_cancels_idle_proxy_handshakes_and_closes_listener()
    {
        var guard = new RetailerBrowserNetworkGuard("coles", new(), CancellationToken.None);
        using var client = new TcpClient(); var port = new Uri(guard.ProxyUrl).Port;
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(new byte[] { 5, 1, 0 });
        var greeting = new byte[2]; await client.GetStream().ReadExactlyAsync(greeting);
        await guard.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]));
        using var another = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => another.ConnectAsync(IPAddress.Loopback, port));
    }
}
