using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Riffle.Playback;
using Riffle.Services;

namespace Riffle.Views;

public partial class SettingsPage : UserControl
{
    Player player = null!;
    MainWindow window = null!;
    UpdateService? updates;
    bool filling;

    public SettingsPage() => InitializeComponent();

    public void Attach(Player target, MainWindow owner)
    {
        player = target;
        window = owner;
        DataContext = target;
        VersionText.Text = $"Riffle {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

        player.PropertyChanged += OnChanged;
        IsVisibleChanged += (_, _) => { if (IsVisible) Fill(); };
        Fill();
    }

    public void AttachUpdates(UpdateService service)
    {
        updates = service;
        updates.Changed += ShowUpdates;
        ShowUpdates();
    }

    void ShowUpdates()
    {
        if (updates is null)
            return;
        UpdateText.Text = updates.State switch
        {
            UpdateState.Checking => "Checking",
            UpdateState.Latest => "Riffle is up to date",
            UpdateState.Available => $"Riffle {updates.Version} is out",
            UpdateState.Downloading => $"Downloading {updates.Version}  ·  {updates.Progress * 100:0}%",
            UpdateState.Ready => $"Riffle {updates.Version} is ready",
            UpdateState.Failed => updates.Error,
            _ => "Not checked yet",
        };
        UpdateButton.Content = updates.State switch
        {
            UpdateState.Ready => "Restart to update",
            UpdateState.Available => "Get it",
            _ => "Check now",
        };
        UpdateButton.IsEnabled = updates.State is not (UpdateState.Checking or UpdateState.Downloading);
    }

    async void OnUpdateButton(object sender, RoutedEventArgs e)
    {
        if (updates is null)
            return;
        switch (updates.State)
        {
            case UpdateState.Ready:
                updates.Install();
                break;
            case UpdateState.Available:
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(updates.PageUrl) { UseShellExecute = true }); }
                catch (Exception) { }
                break;
            default:
                await updates.CheckAsync();
                break;
        }
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Player.NeverCount) or nameof(Player.SignedIn) or nameof(Player.AdsBlocked))
            Fill();
    }

    void Fill()
    {
        filling = true;

        (player.Mode switch { "SpreadArtists" => ModeSpread, "FreshFirst" => ModeFresh, _ => ModeTrue }).IsChecked = true;
        (player.LongestMinutes switch { 15 => Long15, 30 => Long30, _ => LongOff }).IsChecked = true;

        int never = player.NeverCount;
        NeverText.Text = never == 0 ? "No songs blocked" : never == 1 ? "1 song blocked" : $"{never:N0} songs blocked";
        NeverButton.Visibility = never > 0 ? Visibility.Visible : Visibility.Collapsed;

        AccountText.Text = player.SignedIn ? "Signed in to SoundCloud" : "Not signed in";
        SignOutButton.Visibility = player.SignedIn ? Visibility.Visible : Visibility.Collapsed;
        AdsToggle.Content = player.AdsBlocked > 0 ? $"Block ads  ·  {player.AdsBlocked:N0} blocked" : "Block ads";

        var library = player.Library;
        Stats.Text = library.TotalPlays == 0
            ? "Nothing counted yet."
            : $"{library.TotalPlays:N0} plays  ·  {library.TotalMinutes / 60:0.#} hours  ·  {library.Plays.Count:N0} different songs";

        filling = false;
    }

    void OnMode(object sender, RoutedEventArgs e)
    {
        if (!filling && sender is RadioButton { Tag: string mode })
            player.Mode = mode;
    }

    void OnLong(object sender, RoutedEventArgs e)
    {
        if (!filling && sender is RadioButton { Tag: string minutes })
            player.LongestMinutes = int.Parse(minutes);
    }

    void OnClearNever(object sender, RoutedEventArgs e) => player.ClearNever();

    void OnSignOut(object sender, RoutedEventArgs e) => player.SignOut();

    void OnQuit(object sender, RoutedEventArgs e) => window.RequestQuit();
}
