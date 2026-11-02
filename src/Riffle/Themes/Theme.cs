using System.Windows.Media;
using System.Windows.Media.Animation;
using Riffle.Services;

namespace Riffle.Themes;

// The accent colors follow the cover art. These brushes are shared and animated in place,
// so everything bound to them drifts to the new colors together. They're bound directly
// and stay out of resource dictionaries, because WPF freezes what goes in there and a
// frozen brush can't animate.
public static class Theme
{
    static readonly TimeSpan Blend = TimeSpan.FromMilliseconds(1600);

    public static SolidColorBrush Accent { get; } = new(Palette.Default.Accent);
    public static SolidColorBrush AccentSoft { get; } = new(WithAlpha(Palette.Default.Accent, 0x30));
    public static SolidColorBrush AccentFaint { get; } = new(WithAlpha(Palette.Default.Accent, 0x16));
    public static SolidColorBrush Deep { get; } = new(Palette.Default.Deep);
    public static SolidColorBrush Glow { get; } = new(Palette.Default.Glow);

    public static Palette Current { get; private set; } = Palette.Default;

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
