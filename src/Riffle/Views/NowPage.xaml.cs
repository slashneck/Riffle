using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Riffle.Playback;
using Riffle.Themes;

namespace Riffle.Views;

public partial class NowPage : UserControl
{
    static readonly (string Label, int Minutes)[] SleepChoices =
    [
        ("Off", 0), ("15 minutes", 15), ("30 minutes", 30), ("1 hour", 60), ("2 hours", 120),
    ];

    Player player = null!;
    bool showingA = true;

    public NowPage() => InitializeComponent();

    public void Attach(Player target)
    {
        player = target;
        DataContext = target;
        player.PropertyChanged += OnChanged;
        player.ArtChanged += () => ShowArt(animate: true);
        Theme.StaticPropertyChanged += (_, e) => { if (e.PropertyName == nameof(Theme.Accent)) UpdateSleep(); };
        player.WaveformChanged += () => Wave.SetSamples(player.Waveform);
        Wave.SeekRequested += player.SeekFraction;
        Wave.Scrubbing += player.ScrubTo;
        ShowArt(animate: false);
        UpdatePlay();
        UpdateVolume();
        UpdateStatus();
        UpdateSleep();
        UpdateStill();
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Player.IsPlaying):
                UpdatePlay();
                UpdateStatus();
                break;
            case nameof(Player.IsLoadingSlowly):
                UpdateLoading();
                UpdateStatus();
                break;
            case nameof(Player.UltraPerformance):
                UpdateStill();
                break;
            case nameof(Player.Volume):
                UpdateVolume();
                break;
            case nameof(Player.RoundLabel):
            case nameof(Player.Notice):
                UpdateStatus();
                break;
            case nameof(Player.IsSleeping):
            case nameof(Player.SleepLabel):
                UpdateSleep();
                break;
        }
    }

    void UpdatePlay()
    {
        PlayButton.Content = player.IsPlaying ? "" : "";
        PlayButton.ToolTip = player.IsPlaying ? "Pause" : "Play";
        AutomationProperties.SetName(PlayButton, player.IsPlaying ? "Pause" : "Play");
    }

    // A song that takes its time: the button rests and says so.
    void UpdateLoading()
    {
        bool slow = player.IsLoadingSlowly;
        PlayButton.IsEnabled = !slow;
        Spinner.Visibility = slow ? Visibility.Visible : Visibility.Collapsed;
        SpinnerTurn.BeginAnimation(RotateTransform.AngleProperty, slow && !Theme.Still
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    void UpdateStill() => PlayButton.Tag = Theme.Still ? "Still" : "Moves";

    void UpdateStatus() => Status.Text = player.Notice.Length > 0 ? player.Notice : player.IsLoadingSlowly ? "Loading song" : player.RoundLabel;

    void UpdateVolume()
    {
        double v = player.Volume;
        VolumeIcon.Text = v <= 0.001 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";
    }

    void UpdateSleep()
    {
        SleepButton.ToolTip = player.SleepLabel;
        SleepButton.Foreground = player.IsSleeping ? Theme.Accent : (Brush)FindResource("MutedTextBrush");
    }

    void ShowArt(bool animate)
    {
        var (nextBorder, oldBorder) = showingA ? (CoverB, CoverA) : (CoverA, CoverB);
        if (Theme.Still)
        {
            nextBorder.BeginAnimation(OpacityProperty, null);
            oldBorder.BeginAnimation(OpacityProperty, null);
            CoverA.Background = CoverB.Background = null;
            return;
        }

        var picture = new ImageBrush(player.Art.Image) { Stretch = Stretch.UniformToFill };
        picture.Freeze();
        nextBorder.Background = picture;
        showingA = !showingA;

        var time = animate ? TimeSpan.FromMilliseconds(700) : TimeSpan.Zero;
        Panel.SetZIndex(nextBorder, 1);
        Panel.SetZIndex(oldBorder, 0);
        nextBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        oldBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, time) { BeginTime = time });
    }

    void OnClose(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
    void OnToggle(object sender, RoutedEventArgs e) => player.Toggle();
    void OnNext(object sender, RoutedEventArgs e) => player.Next();
    void OnPrevious(object sender, RoutedEventArgs e) => player.Previous();
    void OnNever(object sender, RoutedEventArgs e) => player.NeverPlayCurrent();
    void OnReshuffle(object sender, RoutedEventArgs e) => player.Reshuffle();

    void OnSleep(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SleepButton, Placement = PlacementMode.Top, HorizontalOffset = -120 };
        if (player.IsSleeping)
            menu.Items.Add(new MenuItem { Header = player.SleepLabel, IsEnabled = false });
        foreach (var (label, minutes) in SleepChoices)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = minutes == 0 && !player.IsSleeping };
            item.Click += (_, _) => player.SleepIn(minutes == 0 ? null : TimeSpan.FromMinutes(minutes));
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
