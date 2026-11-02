using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Riffle.Playback;

namespace Riffle.Views;

public partial class SettingsPage : UserControl
{
    static readonly string[] BandNames = ["80", "250", "1k", "4k", "12k"];

    Player player = null!;
    MainWindow window = null!;
    bool filling;
    readonly List<Controls.DragSlider> bands = [];

    public SettingsPage() => InitializeComponent();

    public void Attach(Player target, MainWindow owner)
    {
        player = target;
        window = owner;
        DataContext = target;
        VersionText.Text = $"Riffle {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

        for (int i = 0; i < BandNames.Length; i++)
        {
            int band = i;
            var slider = new Controls.DragSlider
            {
                Orientation = Orientation.Vertical, Minimum = -8, Maximum = 8, Height = 72,
                HorizontalAlignment = HorizontalAlignment.Center, IsMoveToPointEnabled = true, Focusable = false,
                Style = (Style)FindResource("EqSlider"), Cursor = System.Windows.Input.Cursors.Hand,
            };
            System.Windows.Automation.AutomationProperties.SetName(slider, BandNames[i] + " Hz");
            slider.ValueChanged += (_, e) =>
            {
                if (!filling)
                    player.SetEqBand(band, e.NewValue);
            };
            bands.Add(slider);

            var column = new StackPanel();
            column.Children.Add(slider);
            column.Children.Add(new TextBlock
            {
                Text = BandNames[i], FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
                Foreground = (Brush)FindResource("MutedTextBrush"),
            });
            EqBands.Children.Add(column);
        }

        player.PropertyChanged += OnChanged;
        IsVisibleChanged += (_, _) => { if (IsVisible) Fill(); };
        Fill();
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Player.EqPreset) or nameof(Player.EqValues) or nameof(Player.NeverCount) or nameof(Player.SignedIn) or nameof(Player.AdsBlocked))
            Fill();
    }

    void Fill()
    {
        filling = true;

        (player.Mode switch { "SpreadArtists" => ModeSpread, "FreshFirst" => ModeFresh, _ => ModeTrue }).IsChecked = true;
        (player.LongestMinutes switch { 15 => Long15, 30 => Long30, _ => LongOff }).IsChecked = true;

        EqFlat.IsChecked = player.EqPreset == "Flat";
        EqWarm.IsChecked = player.EqPreset == "Warm";
        EqBass.IsChecked = player.EqPreset == "Bass";
        EqClear.IsChecked = player.EqPreset == "Clear";
        EqNight.IsChecked = player.EqPreset == "Night";
        for (int i = 0; i < bands.Count; i++)
            bands[i].Value = player.EqValues[i];

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

    void OnPreset(object sender, RoutedEventArgs e)
    {
        if (!filling && sender is RadioButton { Content: string preset })
            player.EqPreset = preset;
    }

    void OnClearNever(object sender, RoutedEventArgs e) => player.ClearNever();

    void OnSignOut(object sender, RoutedEventArgs e) => player.SignOut();

    void OnQuit(object sender, RoutedEventArgs e) => window.RequestQuit();
}
