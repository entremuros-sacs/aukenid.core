namespace Aukenid.Core.Services;

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

public sealed class WebFetchService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private const int MaxBytes = 200 * 1024;
    internal const int MaxRedirects = 5;
    internal static readonly HttpRequestOptionsKey<IPAddress[]> PinnedAddressesKey = new("Aukenid.PinnedAddresses");

    private readonly HttpClient _http;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    public WebFetchService()
        : this(CreatePinnedHandler(), null)
    {
    }

    internal WebFetchService(HttpMessageHandler handler, Func<string, CancellationToken, Task<IPAddress[]>>? resolve)
    {
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout };
        _resolve = resolve ?? ResolveDns;
    }

    public async Task<string> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Invalid URL.");
        }

        try
        {
            var current = await AuthorizeAsync(uri, cancellationToken);
            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current.Uri);
                request.Options.Set(PinnedAddressesKey, current.Addresses);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirects >= MaxRedirects)
                    {
                        throw new InvalidOperationException("Too many redirects.");
                    }

                    if (response.Headers.Location is not Uri location)
                    {
                        throw new InvalidOperationException("Fetch failed.");
                    }

                    var next = location.IsAbsoluteUri ? location : new Uri(current.Uri, location);
                    current = await AuthorizeAsync(next, cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Fetch failed.");
                }

                var html = await ReadLimitedAsync(response, cancellationToken);
                var text = Regex.Replace(html, "<.*?>", " ");
                text = Regex.Replace(text, "\\s+", " ").Trim();
                return text.Length > 4096 ? text[..4096] : text;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Fetch failed.");
        }
    }

    public static bool IsBlockedHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        var name = host.Trim().TrimEnd('.');
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(name, out var ipAddress) && IsBlockedAddress(ipAddress);
    }

    private async Task<AuthorizedTarget> AuthorizeAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!uri.IsAbsoluteUri
            || (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Only http(s) URLs are allowed.");
        }

        if (IsBlockedHost(uri.Host) || string.IsNullOrEmpty(uri.IdnHost))
        {
            throw new InvalidOperationException("Blocked host.");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.IdnHost, out var literal))
        {
            RejectBlockedPins([literal]);
            addresses = [literal];
        }
        else
        {
            IPAddress[] resolved;
            try
            {
                resolved = await _resolve(uri.DnsSafeHost, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                throw new InvalidOperationException("Fetch failed.");
            }

            RejectBlockedPins(resolved);
            addresses = resolved;
        }

        return new AuthorizedTarget(WithoutUserInfo(uri), addresses);
    }

    // Called again on the socket path so a private address cannot be dialed
    // even if it was attached after the name was checked.
    internal static void RejectBlockedPins(IPAddress[] addresses)
    {
        if (addresses.Length == 0 || addresses.Any(IsBlockedAddress))
        {
            throw new InvalidOperationException("Blocked host.");
        }
    }

    private static bool IsBlockedAddress(IPAddress ipAddress)
    {
        if (ipAddress.IsIPv4MappedToIPv6)
        {
            ipAddress = ipAddress.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ipAddress)
            || ipAddress.Equals(IPAddress.Any)
            || ipAddress.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ipAddress.GetAddressBytes();
            return bytes[0] switch
            {
                0 => true,
                10 => true,
                127 => true,
                169 when bytes[1] == 254 => true,
                172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
                192 when bytes[1] == 168 => true,
                >= 224 => true,
                _ => false,
            };
        }

        if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ipAddress.IsIPv6LinkLocal || ipAddress.IsIPv6SiteLocal || ipAddress.IsIPv6Multicast)
            {
                return true;
            }

            var bytes = ipAddress.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC;
        }

        return true;
    }

    private static SocketsHttpHandler CreatePinnedHandler() =>
        new()
        {
            AllowAutoRedirect = false,
            ConnectCallback = ConnectPinned,
        };

    // Dial the addresses checked for this hop. Do not resolve the name again:
    // a second lookup can return a private or metadata address. TLS still uses
    // the request hostname, so the certificate is checked against that name.
    private static async ValueTask<Stream> ConnectPinned(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(PinnedAddressesKey, out var addresses) || addresses is null)
        {
            throw new InvalidOperationException("Blocked host.");
        }

        RejectBlockedPins(addresses);

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                socket.Dispose();
            }
        }

        throw last ?? new InvalidOperationException("Fetch failed.");
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long length && length > MaxBytes)
        {
            throw new InvalidOperationException("Response too large.");
        }

        var charset = response.Content.Headers.ContentType?.CharSet;
        Encoding encoding;
        try
        {
            encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaxBytes)
            {
                throw new InvalidOperationException("Response too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return encoding.GetString(buffer.ToArray());
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static Uri WithoutUserInfo(Uri uri)
    {
        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Fragment = string.Empty };
        return builder.Uri;
    }

    private static Task<IPAddress[]> ResolveDns(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);

    private readonly record struct AuthorizedTarget(Uri Uri, IPAddress[] Addresses);
}
