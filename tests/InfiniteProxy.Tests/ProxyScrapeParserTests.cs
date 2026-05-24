using InfiniteProxy;
using InfiniteProxy.Sources;
using Xunit;

namespace InfiniteProxy.Tests;

public sealed class ProxyScrapeParserTests
{
    [Fact]
    public void ParseProxyList_parses_ip_port_lines()
    {
        const string body = """
            192.168.1.1:8080
            10.0.0.2:3128
            invalid-line
            # comment
            203.0.113.5:80
            """;

        var proxies = ProxyScrapeSource.ParseProxyList(body, ProxyType.Http);

        Assert.Equal(3, proxies.Count);
        Assert.Contains(proxies, p => p.Host == "192.168.1.1" && p.Port == 8080);
        Assert.Contains(proxies, p => p.Host == "10.0.0.2" && p.Port == 3128);
        Assert.All(proxies, p => Assert.Equal(ProxyType.Http, p.Type));
    }

    [Fact]
    public void ParseInfo_reads_count_and_last_updated()
    {
        const string json = """
            {
              "count": 42,
              "last_updated": 1700000000
            }
            """;

        var info = ProxyScrapeSource.ParseInfo(json, ProxyType.Socks5);

        Assert.NotNull(info);
        Assert.Equal(42, info!.Count);
        Assert.Equal(ProxyType.Socks5, info.Type);
        Assert.NotNull(info.Fingerprint);
    }
}
