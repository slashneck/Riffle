using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Riffle.Controls;
using Riffle.Playback;

namespace Riffle.Views;

public partial class SoundPage : UserControl
{
    Player player = null!;
    bool filling;

    public SoundPage() => InitializeComponent();

    public void Attach(Player target)
    {
        player = target;
        DataContext = target;
        EqCurve.Levels = () => player.Spectrum;
        Curve.BandChanged += (_, _) => player.BandsEdited();
        player.PropertyChanged += OnChanged;
        Fill();
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Player.EqPreset) or nameof(Player.Bands) or nameof(Player.Boost))
            Fill();
    }

    void Fill()
    {
        filling = true;
        Curve.SetBands(player.Bands);
        EqFlat.IsChecked = player.EqPreset == "Flat";
        EqWarm.IsChecked = player.EqPreset == "Warm";
        EqBass.IsChecked = player.EqPreset == "Bass";
        EqClear.IsChecked = player.EqPreset == "Clear";
        EqNight.IsChecked = player.EqPreset == "Night";
        (player.Boost switch { 0 => LoudNormal, >= 10 => LoudMax, _ => LoudLoud }).IsChecked = true;
        filling = false;
    }

    void OnPreset(object sender, RoutedEventArgs e)
    {
        if (!filling && sender is RadioButton { Content: string preset })
            player.EqPreset = preset;
    }

    void OnLoudness(object sender, RoutedEventArgs e)
    {
        if (!filling && sender is RadioButton { Tag: string boost })
            player.Boost = double.Parse(boost, CultureInfo.InvariantCulture);
    }
}
