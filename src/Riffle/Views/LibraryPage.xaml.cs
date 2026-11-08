using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Riffle.Core;
using Riffle.Playback;

namespace Riffle.Views;

public sealed class PickerRow(string title, SoundCloudLink link, bool available) : INotifyPropertyChanged
{
    bool picked;

    public string Title { get; } = title;
    public SoundCloudLink Link { get; } = link;
    public bool Available { get; } = available;
    public bool Picked { get => picked; set { picked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Picked))); } }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class LibraryPage : UserControl
{
    const double DragStart = 7;
    const double TileHeight = 160;

    Player player = null!;
    MainWindow window = null!;
    bool adding;
    SourceTile? pressed;
    Point pressedAt;
    bool dragged;

    public LibraryPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (!IsVisible) Picker.Visibility = Visibility.Collapsed; };
    }

    public void Attach(Player target, MainWindow owner)
    {
        player = target;
        window = owner;
        Tiles.ItemsSource = player.Tiles;
        player.PropertyChanged += OnChanged;
        player.Tiles.CollectionChanged += (_, change) =>
        {
            UpdateAll();
            // a new playlist lands at the end, show it
            if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                Dispatcher.BeginInvoke(() => TileScroller.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        UpdateAll();
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Player.ShuffleLabel):
            case nameof(Player.SelectionDetail):
            case nameof(Player.UltraPerformance):
                Tiles.ItemsSource = null;
                Tiles.ItemsSource = player.Tiles;
                break;
            case nameof(Player.SignedIn):
            case nameof(Player.AccountName):
            case nameof(Player.Mixes):
                UpdateAll();
                break;
        }
    }

    void UpdateAll()
    {
        bool any = player.HasSources;
        Welcome.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ActionArea.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

        ShuffleButton.Content = player.ShuffleLabel;
        ShuffleButton.IsEnabled = player.CanShuffle;
        Detail.Text = player.SelectionDetail;
        DetailRow.Visibility = player.Library.Selected.Count > 1 || Detail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveMixButton.Visibility = player.Library.Selected.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        AccountButton.Content = !player.SignedIn ? "Sign in" : player.AccountName is { } name ? $"Add from {Short(name)}" : "Add from my account";

        MixRow.ItemsSource = null;
        MixRow.ItemsSource = player.Mixes;
        MixRow.Visibility = player.Mixes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void OnLinkKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || adding)
            return;

        var text = LinkBox.Text.Trim();
        if (text.Length == 0)
            return;

        adding = true;
        LinkBox.Text = "";
        Note("");
        try
        {
            var link = await player.UnderstandLinkAsync(text);
            switch (link.Kind)
            {
                case LinkKind.Invalid:
                    Note("That isn't a SoundCloud playlist link");
                    break;
                case LinkKind.Track:
                    Note("That's a single song. Open the playlist and copy its link.");
                    break;
                case LinkKind.Profile:
                    await ShowPickerAsync(link, null);
                    break;
                default:
                    if (player.Library.Sources.Any(s => s.Url == link.Url))
                        Note("Already in your library");
                    _ = player.AddAsync(link);
                    break;
            }
        }
        finally
        {
            adding = false;
        }
    }

    void Note(string text)
    {
        LinkNote.Text = text;
        LinkNote.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void OnAccount(object sender, RoutedEventArgs e)
    {
        if (!player.SignedIn)
        {
            window.HoldOpen(true);
            var signIn = new SignInWindow(player.Host);
            signIn.ShowDialog();
            window.HoldOpen(false);
            await player.RefreshSignInAsync();
            window.ShowFlyout();
            return;
        }

        await ShowPickerAsync(default, "https://soundcloud.com/you/sets");
    }

    async Task ShowPickerAsync(SoundCloudLink profile, string? page)
    {
        Note("Looking for playlists");
        var found = await player.FindPlaylistsAsync(profile, page, n => Note($"Found {n} playlists"));
        Note("");
        if (found.Count == 0)
        {
            Note("No playlists found there");
            return;
        }

        var have = player.Library.Sources.Select(s => s.Url).ToHashSet();
        PickerList.ItemsSource = found.Select(f => new PickerRow(
            have.Contains(f.Link.Url) ? f.Title + "  ·  already added" : f.Title, f.Link, !have.Contains(f.Link.Url))).ToList();
        Picker.Visibility = Visibility.Visible;
    }

    void OnPickerClose(object sender, RoutedEventArgs e) => Picker.Visibility = Visibility.Collapsed;

    void OnPickerAdd(object sender, RoutedEventArgs e)
    {
        Picker.Visibility = Visibility.Collapsed;
        if (PickerList.ItemsSource is not IEnumerable<PickerRow> rows)
            return;
        foreach (var row in rows.Where(r => r.Picked && r.Available))
            _ = player.AddAsync(row.Link);
    }

    void OnShuffle(object sender, RoutedEventArgs e)
    {
        player.ShuffleSelection();
        window.ShowPage("now");
    }

    // ---- tiles: click selects, press and drag moves it to a new spot ----

    static bool InsideButton(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (node is Button)
                return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    static SourceTile? TileAt(object? source) => (source as FrameworkElement)?.DataContext as SourceTile
        ?? ((source as FrameworkContentElement)?.DataContext as SourceTile);

    void OnTilesDown(object sender, MouseButtonEventArgs e)
    {
        if (InsideButton(e.OriginalSource))
            return;
        pressed = TileAt(e.OriginalSource);
        pressedAt = e.GetPosition(Tiles);
        dragged = false;
    }

    void OnTilesMove(object sender, MouseEventArgs e)
    {
        if (pressed is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var at = e.GetPosition(Tiles);
        if (!dragged)
        {
            if ((at - pressedAt).Length < DragStart)
                return;
            dragged = true;
            Tiles.CaptureMouse();
            Tiles.Cursor = Cursors.SizeAll;
        }

        int column = at.X > Tiles.ActualWidth / 2 ? 1 : 0;
        int row = Math.Max(0, (int)(at.Y / TileHeight));
        int target = Math.Min(row * 2 + column, player.Tiles.Count - 1);
        player.MoveTile(pressed, target);

        // keep the spot under the mouse in view when dragging to the edges
        var inView = e.GetPosition(TileScroller);
        if (inView.Y < 30)
            TileScroller.ScrollToVerticalOffset(TileScroller.VerticalOffset - 12);
        else if (inView.Y > TileScroller.ActualHeight - 30)
            TileScroller.ScrollToVerticalOffset(TileScroller.VerticalOffset + 12);
    }

    void OnTilesUp(object sender, MouseButtonEventArgs e)
    {
        var tile = pressed;
        pressed = null;
        Tiles.Cursor = null;
        if (dragged)
        {
            dragged = false;
            Tiles.ReleaseMouseCapture();
            player.Library.Save();
            e.Handled = true;
            return;
        }
        if (tile is not null && !InsideButton(e.OriginalSource) && TileAt(e.OriginalSource) == tile)
            player.ToggleSource(tile);
    }

    void OnRefreshTile(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SourceTile tile })
            _ = player.SyncAsync(tile, manual: true);
    }

    void OnRemoveTile(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SourceTile tile })
            player.RemoveSource(tile);
    }

    void OnTileMenu(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SourceTile tile } host)
            return;

        var menu = new ContextMenu { PlacementTarget = host };

        void Favor(string text, double weight)
        {
            var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = Math.Abs(tile.Source.Weight - weight) < 0.01 };
            item.Click += (_, _) => player.SetWeight(tile, weight);
            menu.Items.Add(item);
        }

        Favor("Equal chance", 1);
        Favor("Comes up more", 2);
        Favor("Comes up most", 4);
        menu.Items.Add(new Separator());

        var sync = new MenuItem { Header = "Check for new songs", Tag = "" };
        sync.Click += (_, _) => _ = player.SyncAsync(tile, manual: true);
        menu.Items.Add(sync);

        var open = new MenuItem { Header = "Open on SoundCloud", Tag = "" };
        open.Click += (_, _) =>
        {
            if (tile.Source.Url.StartsWith("https://soundcloud.com/", StringComparison.Ordinal))
            {
                try { Process.Start(new ProcessStartInfo(tile.Source.Url) { UseShellExecute = true }); }
                catch (Exception) { }
            }
        };
        menu.Items.Add(open);

        var remove = new MenuItem { Header = "Remove", Tag = "" };
        remove.Click += (_, _) => player.RemoveSource(tile);
        menu.Items.Add(remove);
        menu.IsOpen = true;
        e.Handled = true;
    }

    void OnSaveMix(object sender, RoutedEventArgs e)
    {
        var names = player.Tiles.Where(t => t.Selected).Select(t => t.Title).Take(2).ToList();
        var name = names.Count == 2 ? $"{Short(names[0])} + {Short(names[1])}" : "Mix";
        if (player.Library.Selected.Count > 2)
            name += $" + {player.Library.Selected.Count - 2}";
        player.SaveMix(name);
    }

    static string Short(string text) => text.Length <= 14 ? text : text[..13].TrimEnd() + "…";

    void OnMix(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SavedMix mix })
            player.ApplyMix(mix);
    }

    void OnMixMenu(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SavedMix mix } host)
            return;
        var menu = new ContextMenu { PlacementTarget = host };
        var remove = new MenuItem { Header = "Delete mix", Tag = "" };
        remove.Click += (_, _) => player.DeleteMix(mix);
        menu.Items.Add(remove);
        menu.IsOpen = true;
        e.Handled = true;
    }
}
