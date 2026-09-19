using System.Net;
using System.Net.Sockets;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Providers.Http;

public sealed class UnsafeRetailerDestinationException() : HttpRequestException("Retailer destination is not permitted.");

public sealed class SafeRetailerConnection
{
    private readonly RetailerCatalog _catalog;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPEndPoint, CancellationToken, ValueTask<Stream>> _connect;

    public SafeRetailerConnection(RetailerCatalog catalog) : this(catalog,
        (host, token) => Dns.GetHostAddressesAsync(host, token), ConnectSocketAsync) { }

    public SafeRetailerConnection(RetailerCatalog catalog,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect)
    {
        _catalog = catalog;
        _resolve = resolve;
        _connect = connect;
    }

    public async ValueTask<Stream> ConnectAsync(DnsEndPoint destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (destination.Port != 443 || _catalog.FindByHost(destination.Host) is null)
            throw new UnsafeRetailerDestinationException();
        var addresses = await _resolve(destination.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !ProductUrlValidator.IsPublicAddress(address)))
            throw new UnsafeRetailerDestinationException();

        SocketException? lastError = null;
        foreach (var address in addresses.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await _connect(new IPEndPoint(address, destination.Port), cancellationToken); }
            catch (SocketException error) { lastError = error; }
        }
        throw lastError ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private static async ValueTask<Stream> ConnectSocketAsync(IPEndPoint destination, CancellationToken token)
    {
        var socket = new Socket(destination.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // Numeric endpoint pins the validated DNS result. SocketsHttpHandler performs normal hostname TLS validation.
            await socket.ConnectAsync(destination, token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}
