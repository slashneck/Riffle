using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Riffle.Core;
using Riffle.Playback;
using Riffle.Themes;

namespace Riffle.Views;

public partial class LibraryPage : UserControl
{
    Player player = null!;
    MainWindow window = null!;
    bool adding;

    public LibraryPage() => InitializeComponent();

    public void Attach(Player target, MainWindow owner)
    {
        player = target;
        window = owner;
        Tiles.ItemsSource = player.Tiles;
        player.PropertyChanged += OnChanged;
        player.Tiles.CollectionChanged += (_, _) => UpdateAll();
        UpdateAll();
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Player.ShuffleLabel):
            case nameof(Player.SelectionDetail):
            case nameof(Player.SignedIn):
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

        AccountButton.Content = player.SignedIn ? "Bring in my music" : "Sign in";

        MixRow.ItemsSource = null;
        MixRow.ItemsSource = player.Mixes;
        MixRow.Visibility = player.Mixes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void OnLinkChanged(object sender, TextChangedEventArgs e)
    {
        // pasting is the whole form: a valid link starts right away
        var text = LinkBox.Text.Trim();
        if (text.Length < 16 || adding || Links.Parse(text).Kind == LinkKind.Invalid)
            return;
        await AddAsync(text);
    }

    async void OnLinkKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && LinkBox.Text.Trim().Length > 0 && !adding)
            await AddAsync(LinkBox.Text.Trim());
    }

    async Task AddAsync(string text)
    {
        adding = true;
        LinkBox.Text = "";
        var problem = await player.AddLinkAsync(text, Note);
        adding = false;
        Note(problem ?? "");
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
            var signIn = new SignInWindow(player.Host) { Owner = window };
            signIn.ShowDialog();
            window.HoldOpen(false);
            await player.RefreshSignInAsync();
            window.Activate();
            if (!player.SignedIn)
                return;
        }

        var problem = await player.ImportSignedInAsync(Note);
        Note(problem ?? "");
    }

    void OnShuffle(object sender, RoutedEventArgs e)
    {
        player.ShuffleSelection();
        window.ShowPage("now");
    }

    void OnTile(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SourceTile tile })
            player.ToggleSource(tile);
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
        sync.Click += (_, _) => _ = player.SyncAsync(tile);
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
