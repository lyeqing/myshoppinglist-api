using System.Net;
using System.Net.Sockets;
using System.Text;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Providers.Http;

// A per-search SOCKS5 tunnel. Chromium retains normal TLS verification; DNS resolution and
// the actual numeric TCP destination are controlled here, not by a preflight DNS check.
public sealed class RetailerBrowserNetworkGuard : IAsyncDisposable
{
    private readonly string _shop;
    private readonly RetailerSearchOptions _options;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPEndPoint, CancellationToken, ValueTask<Stream>> _connect;
    private readonly CancellationTokenSource _stop;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<Task> _connections = [];
    private readonly Task _accept;
    private long _bytes;
    private int _limitExceeded;
    public bool LimitExceeded => Volatile.Read(ref _limitExceeded) != 0;
    public string ProxyUrl => "socks5://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;

    public RetailerBrowserNetworkGuard(string shop, RetailerSearchOptions options, CancellationToken token,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<IPEndPoint, CancellationToken, ValueTask<Stream>>? connect = null)
    {
        _shop = shop; _options = options;
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        _connect = connect ?? ConnectSocketAsync;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _listener.Start(32);
        _accept = AcceptAsync();
    }

    public static bool IsAllowedHost(string shop, string host) => shop switch
    {
        "coles" => host.Equals("www.coles.com.au", StringComparison.OrdinalIgnoreCase)
            || host.Equals("coles.com.au", StringComparison.OrdinalIgnoreCase),
        "woolworths" => host.Equals("www.woolworths.com.au", StringComparison.OrdinalIgnoreCase)
            || host.Equals("woolworths.com.au", StringComparison.OrdinalIgnoreCase)
            || host.Equals("cdn0.woolworths.media", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    public static bool IsAllowedRequest(string shop, string url, string method, string resourceType, bool navigation)
    {
        if (url.Length > 8192 || url.Contains('\\') || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0
            || uri.HostNameType != UriHostNameType.Dns || !IsAllowedHost(shop, uri.IdnHost)) return false;
        if (navigation) return method == "GET" && IsSearchPage(shop, uri);
        if (resourceType is "image" or "media" or "font" or "websocket" or "eventsource" or "ping") return false;
        if (method == "GET") return resourceType is "script" or "stylesheet" or "xhr" or "fetch";
        // Woolworths' public search service uses a read-only POST. No cart, login, or write routes.
        return shop == "woolworths" && uri.IdnHost == "www.woolworths.com.au" && method == "POST"
            && uri.AbsolutePath.Equals("/apis/ui/Search/products", StringComparison.OrdinalIgnoreCase)
            && resourceType is "xhr" or "fetch";
    }

    public static bool IsSearchPage(string shop, Uri uri) => uri.Scheme == "https" && uri.Port == 443
        && uri.UserInfo.Length == 0 && uri.IdnHost == $"www.{shop}.com.au"
        && uri.AbsolutePath == (shop == "coles" ? "/search/products" : "/shop/search/products")
        && shop is "coles" or "woolworths";

    public async ValueTask<Stream> ConnectPublicAsync(string host, int port, CancellationToken token)
    {
        if (port != 443 || !IsAllowedHost(_shop, host)) throw new UnsafeRetailerDestinationException();
        var addresses = await _resolve(host, token);
        if (addresses.Length == 0 || addresses.Any(a => !ProductUrlValidator.IsPublicAddress(a)))
            throw new UnsafeRetailerDestinationException();
        SocketException? last = null;
        foreach (var address in addresses.Distinct())
        {
            try { return await _connect(new(address, port), token); }
            catch (SocketException error) { last = error; }
        }
        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private async Task AcceptAsync()
    {
        try
        {
            var count = 0;
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (++count > _options.MaximumRequests * 2)
                { client.Dispose(); ExceedLimit(); break; }
                _connections.Add(HandleAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            var token = deadline.Token;
            try
            {
                var stream = client.GetStream();
                var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, token);
                if (greeting[0] != 5 || greeting[1] == 0) return;
                var methods = new byte[greeting[1]]; await stream.ReadExactlyAsync(methods, token);
                if (!methods.Contains((byte)0)) { await stream.WriteAsync(new byte[] { 5, 255 }, token); return; }
                await stream.WriteAsync(new byte[] { 5, 0 }, token);
                var request = new byte[4]; await stream.ReadExactlyAsync(request, token);
                // CONNECT + DNS name only. BIND, UDP, and literal-IP connections are rejected.
                if (request[0] != 5 || request[1] != 1 || request[2] != 0 || request[3] != 3)
                { await RejectAsync(stream, token); return; }
                var length = new byte[1]; await stream.ReadExactlyAsync(length, token);
                var hostBytes = new byte[length[0]]; await stream.ReadExactlyAsync(hostBytes, token);
                if (hostBytes.Any(b => b > 127)) { await RejectAsync(stream, token); return; }
                var portBytes = new byte[2]; await stream.ReadExactlyAsync(portBytes, token);
                await using var upstream = await ConnectPublicAsync(Encoding.ASCII.GetString(hostBytes),
                    (portBytes[0] << 8) | portBytes[1], token);
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
                var up = RelayAsync(stream, upstream, token); var down = RelayAsync(upstream, stream, token);
                await Task.WhenAny(up, down);
                await deadline.CancelAsync();
                try { await Task.WhenAll(up, down); } catch (OperationCanceledException) { }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or HttpRequestException or ObjectDisposedException)
            { /* Closing the socket fails this request; no direct-network fallback is allowed. */ }
        }
    }

    private async Task RelayAsync(Stream source, Stream destination, CancellationToken token)
    {
        var buffer = new byte[16384];
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            if (Interlocked.Add(ref _bytes, count) > _options.MaximumTransferBytes)
            { ExceedLimit(); return; }
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }

    private void ExceedLimit() { Interlocked.Exchange(ref _limitExceeded, 1); _stop.Cancel(); }
    private static ValueTask RejectAsync(Stream stream, CancellationToken token) =>
        stream.WriteAsync(new byte[] { 5, 2, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
    private static async ValueTask<Stream> ConnectSocketAsync(IPEndPoint endpoint, CancellationToken token)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { await socket.ConnectAsync(endpoint, token); return new NetworkStream(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); _listener.Stop();
        await _accept; await Task.WhenAll(_connections);
        _stop.Dispose();
    }
}
