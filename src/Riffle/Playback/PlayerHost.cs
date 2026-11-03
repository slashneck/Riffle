using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Riffle.Shell;

namespace Riffle.Playback;

// A browser engine that's never on screen, holding SoundCloud's official players.
// Everything it needs to do goes through messages to and from player.html.
public sealed partial class PlayerHost : IDisposable
{
    const string LocalHost = "riffle.local";

    readonly Window window;
    readonly WebView2 view = new();
    CoreWebView2Environment? environment;
    bool ready;
    readonly List<string> waiting = [];

    public PlayerHost()
    {
        window = new Window
        {
            Width = 340, Height = 400, Left = -6000, Top = -6000,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Content = view, Title = "Riffle engine",
        };
    }

    public bool BlockAds { get; set; } = true;
    public int AdsBlocked { get; private set; }
    public string? LastUser { get; private set; }
    public CoreWebView2Environment? Environment => environment;

    public event Action<JsonElement>? Message;
    public event Action? AdBlocked;

    public static string DataFolder => Path.Combine(Services.Settings.LocalFolder, "WebView");

    public async Task StartAsync()
    {
        window.Show();
        Native.AddExStyle(new WindowInteropHelper(window).Handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);

        // Cache turned down to nothing so songs don't end up on disk.
        var options = new CoreWebView2EnvironmentOptions(
            "--autoplay-policy=no-user-gesture-required --disk-cache-size=1 --media-cache-size=1 " +
            "--disable-background-timer-throttling --disable-renderer-backgrounding " +
            "--disable-backgrounding-occluded-windows --disable-features=CalculateNativeWinOcclusion");
        environment = await CoreWebView2Environment.CreateAsync(null, DataFolder, options);
        await view.EnsureCoreWebView2Async(environment);

        var web = view.CoreWebView2;
        web.Settings.AreDefaultContextMenusEnabled = false;
        web.Settings.AreDevToolsEnabled = false;
        web.Settings.IsStatusBarEnabled = false;
        await web.AddScriptToExecuteOnDocumentCreatedAsync(Resource("Riffle.tap.js"));
        Watch(web);
        web.WebMessageReceived += OnMessage;
        web.Navigate($"https://{LocalHost}/player.html");
    }

    // Serves our page and turns away anything on the ad list, for every view we create.
    public void Watch(CoreWebView2 web)
    {
        web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        web.WebResourceRequested += (_, e) =>
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri))
                return;

            if (uri.Host == LocalHost)
            {
                var bytes = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Resource("Riffle.player.html")));
                e.Response = environment!.CreateWebResourceResponse(bytes, 200, "OK", "Content-Type: text/html; charset=utf-8");
                return;
            }

            if (BlockAds && IsAd(uri))
            {
                e.Response = environment!.CreateWebResourceResponse(null, 403, "Blocked", "");
                AdsBlocked++;
                Application.Current.Dispatcher.BeginInvoke(() => AdBlocked?.Invoke());
            }
        };
    }

    static bool IsAd(Uri uri) =>
        AdHosts().IsMatch(uri.Host) || AdPaths().IsMatch(uri.AbsolutePath);

    [GeneratedRegex(@"(^|\.)(doubleclick\.net|googlesyndication\.com|googleadservices\.com|adswizz\.com|adsrvr\.org|moatads\.com|amazon-adsystem\.com|adnxs\.com|rubiconproject\.com|pubmatic\.com|openx\.net|criteo\.com|scorecardresearch\.com|promoted\.soundcloud\.com|ads\.soundcloud\.com)$", RegexOptions.IgnoreCase)]
    private static partial Regex AdHosts();

    [GeneratedRegex(@"/(audio_ad|video_ad|vast|promoted)(/|$|s/)", RegexOptions.IgnoreCase)]
    private static partial Regex AdPaths();

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var message = doc.RootElement;
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("t", out var type))
            return;

        if (type.GetString() == "ready")
        {
            ready = true;
            foreach (var pending in waiting)
                view.CoreWebView2.PostWebMessageAsJson(pending);
            waiting.Clear();
        }
        Message?.Invoke(message);
    }

    public void Send(object command)
    {
        var json = JsonSerializer.Serialize(command);
        if (ready)
            view.CoreWebView2.PostWebMessageAsJson(json);
        else
            waiting.Add(json);
    }

    public async Task<bool> IsSignedInAsync()
    {
        if (view.CoreWebView2 is not { } web)
            return false;
        var cookies = await web.CookieManager.GetCookiesAsync("https://soundcloud.com");
        return cookies.Any(c => c.Name == "oauth_token");
    }

    public void SignOut()
    {
        view.CoreWebView2?.CookieManager.DeleteAllCookies();
    }

    // Reads the playlist links off a profile page. Only used when importing.
    public async Task<List<(string Url, string Title)>> ScanLinksAsync(string pageUrl, Action<int>? progress, CancellationToken token)
    {
        var scanner = new WebView2();
        var host = new Window
        {
            Width = 1200, Height = 1600, Left = -8000, Top = -8000, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Content = scanner, Title = "Riffle import",
        };
        host.Show();
        Native.AddExStyle(new WindowInteropHelper(host).Handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);

        try
        {
            await scanner.EnsureCoreWebView2Async(environment);
            scanner.CoreWebView2.IsMuted = true;
            Watch(scanner.CoreWebView2);

            var loaded = new TaskCompletionSource();
            scanner.CoreWebView2.NavigationCompleted += (_, _) => loaded.TrySetResult();
            scanner.CoreWebView2.Navigate(pageUrl);
            await Task.WhenAny(loaded.Task, Task.Delay(20000, token));
            await Task.Delay(2500, token);

            const string read = """
                (() => { window.scrollTo(0, document.body.scrollHeight);
                  const main = document.querySelector('#content') || document.body;
                  const seen = new Map();
                  for (const a of main.querySelectorAll('a[href*="/sets/"]')) {
                    const href = a.href;
                    if (href.includes('?in=') || href.includes('/discover/')) continue;
                    const text = (a.textContent || '').trim();
                    if (!seen.has(href) || (text && !seen.get(href))) seen.set(href, text);
                  }
                  return JSON.stringify([...seen.entries()]); })()
                """;

            var found = new List<(string Url, string Title)>();
            int still = 0;
            while (still < 4 && !token.IsCancellationRequested)
            {
                var raw = await scanner.CoreWebView2.ExecuteScriptAsync(read);
                var list = JsonSerializer.Deserialize<List<List<string>>>(JsonSerializer.Deserialize<string>(raw) ?? "[]") ?? [];
                still = list.Count == found.Count ? still + 1 : 0;
                found = list.Select(e => (e[0], e[1])).ToList();
                progress?.Invoke(found.Count);
                await Task.Delay(1200, token);
            }
            var who = await scanner.CoreWebView2.ExecuteScriptAsync(
                "(document.querySelector('a.header__userNavUsernameButton') || {}).pathname || ''");
            LastUser = JsonSerializer.Deserialize<string>(who)?.Trim('/');
            return found;
        }
        finally
        {
            scanner.Dispose();
            host.Close();
        }
    }

    static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " is missing from the build");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        view.Dispose();
        window.Close();
    }
}
