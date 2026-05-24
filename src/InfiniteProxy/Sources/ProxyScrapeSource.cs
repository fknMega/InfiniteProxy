using System.Globalization;
using System.Net;
using System.Text.Json;

namespace InfiniteProxy.Sources;

/// <summary>
/// Fetches free proxy lists from the ProxyScrape public API.
/// </summary>
/// <remarks>
/// API docs: https://docs.proxyscrape.com/api-reference/public-api/get-proxy-list
/// </remarks>
public sealed class ProxyScrapeSource : IProxySource, IDisposable
{
    private const string BaseUrl = "https://api.proxyscrape.com/v4/free-proxy-list/get";
    private readonly HttpClient _httpClient;
    private readonly InfiniteProxyOptions _options;
    private readonly bool _ownsHttpClient;

    public ProxyScrapeSource(InfiniteProxyOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        if (httpClient is null)
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
        }
    }

    public string Name => "ProxyScrape";

    public async Task<IReadOnlyList<ProxyCandidate>> FetchAsync(
        ProxyType type,
        CancellationToken cancellationToken = default)
    {
        var url = BuildUrl(type, "getproxies");
        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseProxyList(body, type);
    }

    public async Task<ProxySourceInfo?> GetInfoAsync(
        ProxyType type,
        CancellationToken cancellationToken = default)
    {
        var url = BuildUrl(type, "proxyinfo");
        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseInfo(body, type);
    }

    private string BuildUrl(ProxyType type, string request)
    {
        var query = new List<string>
        {
            $"request={request}",
            $"protocol={ToProtocol(type)}",
            $"timeout={_options.SourceTimeoutMs}",
            $"limit={_options.SourceLimit}",
            "skip=0"
        };

        if (!string.IsNullOrWhiteSpace(_options.Country))
        {
            query.Add($"country={Uri.EscapeDataString(_options.Country)}");
        }
        else
        {
            query.Add("country=all");
        }

        return $"{BaseUrl}?{string.Join('&', query)}";
    }

    private static string ToProtocol(ProxyType type) => type switch
    {
        ProxyType.Http => "http",
        ProxyType.Socks4 => "socks4",
        ProxyType.Socks5 => "socks5",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported proxy type.")
    };

    internal static IReadOnlyList<ProxyCandidate> ParseProxyList(string body, ProxyType type)
    {
        var results = new List<ProxyCandidate>();
        using var reader = new StringReader(body);

        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.LastIndexOf(':');
            if (separator <= 0 || separator >= line.Length - 1)
            {
                continue;
            }

            var host = line[..separator].Trim();
            var portText = line[(separator + 1)..].Trim();

            if (!IPAddress.TryParse(host, out _) || !int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
            {
                continue;
            }

            if (port is <= 0 or > 65535)
            {
                continue;
            }

            results.Add(new ProxyCandidate
            {
                Host = host,
                Port = port,
                Type = type,
                Source = "ProxyScrape"
            });
        }

        return results;
    }

    internal static ProxySourceInfo? ParseInfo(string body, ProxyType type)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var count = root.TryGetProperty("count", out var countElement) && countElement.TryGetInt32(out var parsedCount)
                ? parsedCount
                : 0;

            DateTimeOffset? lastUpdated = null;
            if (root.TryGetProperty("last_updated", out var updatedElement))
            {
                if (updatedElement.ValueKind == JsonValueKind.Number &&
                    updatedElement.TryGetInt64(out var unixSeconds))
                {
                    lastUpdated = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                }
                else if (updatedElement.ValueKind == JsonValueKind.String &&
                         DateTimeOffset.TryParse(updatedElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
                {
                    lastUpdated = parsedDate;
                }
            }

            var fingerprint = lastUpdated?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? count.ToString(CultureInfo.InvariantCulture);

            return new ProxySourceInfo
            {
                SourceName = "ProxyScrape",
                Type = type,
                Count = count,
                LastUpdated = lastUpdated,
                Fingerprint = fingerprint
            };
        }
        catch (JsonException)
        {
            return new ProxySourceInfo
            {
                SourceName = "ProxyScrape",
                Type = type,
                Count = 0,
                Fingerprint = body.GetHashCode().ToString(CultureInfo.InvariantCulture)
            };
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
