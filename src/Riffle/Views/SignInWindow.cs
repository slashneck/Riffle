using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using Riffle.Playback;

namespace Riffle.Views;

// SoundCloud's own sign-in page. Riffle never sees what gets typed here, it only checks
// whether the session cookie exists afterwards.
public sealed class SignInWindow : Window
{
    readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly WebView2 view = new();

    public SignInWindow(PlayerHost host)
    {
        Title = "Sign in to SoundCloud";
        Width = 480;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BaseBrush");
        Content = view;

        Loaded += async (_, _) =>
        {
            await view.EnsureCoreWebView2Async(host.Environment);
            host.Watch(view.CoreWebView2);
            view.CoreWebView2.Navigate("https://soundcloud.com/signin");
            poll.Start();
        };

        poll.Tick += async (_, _) =>
        {
            var cookies = await view.CoreWebView2.CookieManager.GetCookiesAsync("https://soundcloud.com");
            if (cookies.Any(c => c.Name == "oauth_token"))
            {
                poll.Stop();
                Close();
            }
        };

        Closed += (_, _) =>
        {
            poll.Stop();
            view.Dispose();
        };
    }
}
