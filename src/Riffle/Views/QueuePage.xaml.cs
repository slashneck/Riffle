using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Riffle.Core;
using Riffle.Playback;
using Riffle.Themes;

namespace Riffle.Views;

public partial class QueuePage : UserControl
{
    readonly DispatcherTimer settle = new() { Interval = TimeSpan.FromMilliseconds(140) };
    Player player = null!;

    public QueuePage()
    {
        InitializeComponent();
        settle.Tick += (_, _) => { settle.Stop(); Refresh(); };
    }

    public void Attach(Player target)
    {
        player = target;
        // quick changes in a row only rebuild the list once
        player.QueueChanged += () => { if (IsVisible) { settle.Stop(); settle.Start(); } };
    }

    public void Refresh()
    {
        var queue = player.Queue;
        var ids = ShowNext.IsChecked == true ? queue.Upcoming : queue.History;
        var tracks = ids.Where(player.Library.Tracks.ContainsKey).Select(id => player.Library.Tracks[id]);

        var term = Search.Text.Trim();
        if (term.Length > 0)
            tracks = tracks.Where(t => t.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                       t.Artist.Contains(term, StringComparison.OrdinalIgnoreCase));

        var list = tracks.ToList();
        Rows.ItemsSource = list;
        Summary.Text = queue.Count == 0 ? "" : player.RoundLabel;
        Empty.Text = list.Count > 0 ? "" : term.Length > 0 ? "No matches" : ShowNext.IsChecked == true ? "Nothing queued yet" : "Nothing played yet";
    }

    void OnTab(object sender, RoutedEventArgs e)
    {
        if (player is not null)
            Refresh();
    }

    void OnSearch(object sender, TextChangedEventArgs e)
    {
        if (player is not null)
        {
            settle.Stop();
            settle.Start();
        }
    }

    void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindButton(source))
            return;
        if (sender is FrameworkElement { DataContext: Track track })
            player.PlayFromQueue(track.Id);
    }

    static bool FindButton(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is Button)
                return true;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    void OnPlayNext(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Track track })
            player.PlayNextInQueue(track.Id);
    }

    void OnNever(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Track track })
            player.NeverPlay(track.Id);
    }

    void OnRowMenu(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Track track } row)
            return;

        var menu = new ContextMenu { PlacementTarget = row };
        Add(menu, "Play now", "", () => player.PlayFromQueue(track.Id));
        Add(menu, "Play next", "", () => player.PlayNextInQueue(track.Id));
        menu.Items.Add(new Separator());
        Add(menu, "Open on SoundCloud", "", () => Open(track.Link));
        Add(menu, "Never play this again", "", () => player.NeverPlay(track.Id));
        menu.IsOpen = true;
    }

    static void Add(ContextMenu menu, string text, string glyph, Action action)
    {
        var item = new MenuItem { Header = text, Tag = glyph };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    static void Open(string? url)
    {
        if (url is null || !url.StartsWith("https://soundcloud.com/", StringComparison.Ordinal))
            return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { }
    }
}
