using System.Net;
using System.Reflection;

namespace Riffle.Services;

// Riffle's own requests: artwork, waveforms and playlist names. No cookies, https only.
static class Net
{
    public static readonly HttpClient Client = Create();

    static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Riffle/{version?.ToString(3) ?? "0"}");
        return client;
    }

    // These urls come from track data we don't control, so only SoundCloud's own
    // media host is accepted.
    public static Uri? SafeMediaUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;
        return uri.Host.EndsWith(".sndcdn.com", StringComparison.OrdinalIgnoreCase) ? uri : null;
    }
}
