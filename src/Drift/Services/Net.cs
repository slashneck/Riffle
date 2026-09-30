using System.Net;
using System.Reflection;

namespace Drift.Services;

// Every request Drift makes goes through this client. No cookies, no auth, https only.
static class Net
{
    public static readonly HttpClient Client = Create();

    static readonly string[] ImageHosts =
    [
        "img.radioparadise.com",
        "ntslive.co.uk",
        "mzstatic.com",
    ];

    static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 4,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Drift/{version?.ToString(3) ?? "0"}");
        return client;
    }

    // Artwork urls come from stream metadata, which we don't control, so only
    // known image hosts are allowed and plain http is upgraded.
    public static Uri? SafeImageUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme == Uri.UriSchemeHttp)
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;

        if (uri.Scheme != Uri.UriSchemeHttps)
            return null;

        foreach (var host in ImageHosts)
        {
            if (uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))
                return uri;
        }
        return null;
    }
}
