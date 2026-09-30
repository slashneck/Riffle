using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Drift.Services;

namespace Drift.Themes;

// The accent colors follow the cover art. Brushes are shared and animated in place,
// so everything bound to them drifts to the new colors together.
public static class Theme
{
    static readonly TimeSpan Blend = TimeSpan.FromMilliseconds(1600);

    public static readonly SolidColorBrush Accent = new(Palette.Default.Accent);
    public static readonly SolidColorBrush AccentSoft = new(WithAlpha(Palette.Default.Accent, 0x30));
    public static readonly SolidColorBrush AccentFaint = new(WithAlpha(Palette.Default.Accent, 0x16));
    public static readonly SolidColorBrush Deep = new(Palette.Default.Deep);
    public static readonly SolidColorBrush Glow = new(Palette.Default.Glow);

    public static Palette Current { get; private set; } = Palette.Default;

    // Application level resources get frozen by WPF, which would stop the color animation,
    // so each window and menu gets the live brushes in its own dictionary instead.
    public static void Install(ResourceDictionary resources)
    {
        resources["AccentBrush"] = Accent;
        resources["AccentSoftBrush"] = AccentSoft;
        resources["AccentFaintBrush"] = AccentFaint;
        resources["DeepBrush"] = Deep;
        resources["GlowBrush"] = Glow;
    }

    public static void Apply(Palette palette)
    {
        if (palette == Current)
            return;
        Current = palette;

        Animate(Accent, palette.Accent);
        Animate(AccentSoft, WithAlpha(palette.Accent, 0x30));
        Animate(AccentFaint, WithAlpha(palette.Accent, 0x16));
        Animate(Deep, palette.Deep);
        Animate(Glow, palette.Glow);
    }

    static void Animate(SolidColorBrush brush, Color to)
    {
        var animation = new ColorAnimation(to, Blend)
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
