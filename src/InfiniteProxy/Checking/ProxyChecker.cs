using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using InfiniteProxy.Sources;

namespace InfiniteProxy.Checking;

/// <summary>
/// Validates proxy candidates by attempting real connections through them.
/// </summary>
public sealed class ProxyChecker
{
    private readonly InfiniteProxyOptions _options;

    public ProxyChecker(InfiniteProxyOptions options)
    {
        _options = options;
    }

    public async Task<ProxyEndpoint?> CheckAsync(
        ProxyCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.CheckTimeout);

        try
        {
            return candidate.Type switch
            {
                ProxyType.Http => await CheckHttpAsync(candidate, timeoutCts.Token).ConfigureAwait(false),
                ProxyType.Socks4 => await CheckSocks4Async(candidate, timeoutCts.Token).ConfigureAwait(false),
                ProxyType.Socks5 => await CheckSocks5Async(candidate, timeoutCts.Token).ConfigureAwait(false),
                _ => null
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<ProxyEndpoint?> CheckHttpAsync(ProxyCandidate candidate, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy(candidate.Host, candidate.Port),
            UseProxy = true
        };

        using var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = _options.CheckTimeout
        };

        using var response = await client.GetAsync(_options.HttpValidationUrl, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        stopwatch.Stop();
        return ToEndpoint(candidate, stopwatch.Elapsed);
    }

    private async Task<ProxyEndpoint?> CheckSocks4Async(ProxyCandidate candidate, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await ConnectAsync(socket, candidate.Host, candidate.Port, cancellationToken).ConfigureAwait(false);

        if (!IPAddress.TryParse(_options.SocksValidationHost, out var targetAddress))
        {
            targetAddress = (await Dns.GetHostAddressesAsync(_options.SocksValidationHost, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

            if (targetAddress is null)
            {
                return null;
            }
        }

        var request = BuildSocks4ConnectRequest(targetAddress, _options.SocksValidationPort);
        await socket.SendAsync(request, SocketFlags.None, cancellationToken).ConfigureAwait(false);

        var responseBuffer = new byte[8];
        var received = await ReceiveExactAsync(socket, responseBuffer, cancellationToken).ConfigureAwait(false);
        if (received < 8 || responseBuffer[1] != 0x5A)
        {
            return null;
        }

        stopwatch.Stop();
        return ToEndpoint(candidate, stopwatch.Elapsed);
    }

    private async Task<ProxyEndpoint?> CheckSocks5Async(ProxyCandidate candidate, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await ConnectAsync(socket, candidate.Host, candidate.Port, cancellationToken).ConfigureAwait(false);

        await socket.SendAsync(new byte[] { 0x05, 0x01, 0x00 }, SocketFlags.None, cancellationToken).ConfigureAwait(false);

        var methodResponse = new byte[2];
        if (await ReceiveExactAsync(socket, methodResponse, cancellationToken).ConfigureAwait(false) < 2 ||
            methodResponse[0] != 0x05 ||
            methodResponse[1] != 0x00)
        {
            return null;
        }

        var hostBytes = Encoding.ASCII.GetBytes(_options.SocksValidationHost);
        var connectRequest = new byte[7 + hostBytes.Length];
        connectRequest[0] = 0x05;
        connectRequest[1] = 0x01;
        connectRequest[2] = 0x00;
        connectRequest[3] = 0x03;
        connectRequest[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(connectRequest, 5);
        connectRequest[^2] = (byte)(_options.SocksValidationPort >> 8);
        connectRequest[^1] = (byte)(_options.SocksValidationPort & 0xFF);

        await socket.SendAsync(connectRequest, SocketFlags.None, cancellationToken).ConfigureAwait(false);

        var connectResponse = new byte[4];
        if (await ReceiveExactAsync(socket, connectResponse, cancellationToken).ConfigureAwait(false) < 4 ||
            connectResponse[0] != 0x05 ||
            connectResponse[1] != 0x00)
        {
            return null;
        }

        var addressType = connectResponse[3];
        var trailingLength = addressType switch
        {
            0x01 => 4 + 2,
            0x03 => 1,
            0x04 => 16 + 2,
            _ => 0
        };

        if (trailingLength == 0)
        {
            return null;
        }

        var trailing = new byte[trailingLength];
        if (addressType == 0x03)
        {
            if (await ReceiveExactAsync(socket, trailing, 1, cancellationToken).ConfigureAwait(false) < 1)
            {
                return null;
            }

            var domainLength = trailing[0];
            trailing = new byte[domainLength + 2];
        }

        if (await ReceiveExactAsync(socket, trailing, cancellationToken).ConfigureAwait(false) < trailing.Length)
        {
            return null;
        }

        stopwatch.Stop();
        return ToEndpoint(candidate, stopwatch.Elapsed);
    }

    private static byte[] BuildSocks4ConnectRequest(IPAddress targetAddress, int port)
    {
        var addressBytes = targetAddress.GetAddressBytes();
        if (addressBytes.Length != 4)
        {
            throw new NotSupportedException("SOCKS4 validation requires an IPv4 address.");
        }

        var request = new byte[9];
        request[0] = 0x04;
        request[1] = 0x01;
        request[2] = (byte)(port >> 8);
        request[3] = (byte)(port & 0xFF);
        addressBytes.CopyTo(request, 4);
        request[8] = 0x00;
        return request;
    }

    private static async Task ConnectAsync(Socket socket, string host, int port, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            return;
        }

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var target = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                     ?? addresses.FirstOrDefault()
                     ?? throw new SocketException((int)SocketError.HostNotFound);

        await socket.ConnectAsync(new IPEndPoint(target, port), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReceiveExactAsync(Socket socket, byte[] buffer, CancellationToken cancellationToken)
        => await ReceiveExactAsync(socket, buffer, buffer.Length, cancellationToken).ConfigureAwait(false);

    private static async Task<int> ReceiveExactAsync(Socket socket, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < length)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(total, length - total), SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (received == 0)
            {
                break;
            }

            total += received;
        }

        return total;
    }

    private static ProxyEndpoint ToEndpoint(ProxyCandidate candidate, TimeSpan latency) =>
        new()
        {
            Host = candidate.Host,
            Port = candidate.Port,
            Type = candidate.Type,
            Source = candidate.Source,
            Latency = latency,
            LastValidated = DateTimeOffset.UtcNow
        };
}
