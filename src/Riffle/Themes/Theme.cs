using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using Riffle.Services;

namespace Riffle.Themes;

// The accent colors follow the cover art. Every brush here is frozen and gets replaced,
// never changed. Changing a brush that's in use makes WPF's render thread notify all that
// listen to it, and now and then one of them is already gone, which takes the whole app down.
public static class Theme
{
    const double BlendMs = 1600;

    static readonly DispatcherTimer Stepper = new() { Interval = TimeSpan.FromMilliseconds(50) };
    static Color fromAccent = Palette.Default.Accent, fromDeep = Palette.Default.Deep, fromGlow = Palette.Default.Glow;
    static Color nowAccent = fromAccent, nowDeep = fromDeep, nowGlow = fromGlow;
    static long began;

    static Theme()
    {
        Stepper.Tick += (_, _) => Step();
        Show(nowAccent, nowDeep, nowGlow);
    }

    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;

    public static Brush Accent { get; private set; } = null!;
    public static Brush AccentSoft { get; private set; } = null!;
    public static Brush AccentFaint { get; private set; } = null!;
    public static Brush Deep { get; private set; } = null!;
    public static Brush GlowHalo { get; private set; } = null!;

    public static Palette Current { get; private set; } = Palette.Default;

    // Ultra performance: nothing moves and nothing is drawn that doesn't have to be.
    public static bool Still { get; set; }

    public static void Apply(Palette palette)
    {
        if (palette == Current)
            return;
        Current = palette;

        if (Still)
        {
            Stepper.Stop();
            Show(palette.Accent, palette.Deep, palette.Glow);
            return;
        }

        (fromAccent, fromDeep, fromGlow) = (nowAccent, nowDeep, nowGlow);
        began = Environment.TickCount64;
        Stepper.Start();
    }

    static void Step()
    {
        double t = Math.Min(1, (Environment.TickCount64 - began) / BlendMs);
        double eased = (1 - Math.Cos(t * Math.PI)) / 2;
        Show(Mix(fromAccent, Current.Accent, eased), Mix(fromDeep, Current.Deep, eased), Mix(fromGlow, Current.Glow, eased));
        if (t >= 1)
            Stepper.Stop();
    }

    static void Show(Color accent, Color deep, Color glow)
    {
        (nowAccent, nowDeep, nowGlow) = (accent, deep, glow);
        Accent = Solid(accent);
        AccentSoft = Solid(WithAlpha(accent, 0x30));
        AccentFaint = Solid(WithAlpha(accent, 0x16));
        Deep = Solid(deep);
        GlowHalo = Halo(glow);
        foreach (var name in new[] { nameof(Accent), nameof(AccentSoft), nameof(AccentFaint), nameof(Deep), nameof(GlowHalo) })
            StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(name));
    }

    static Brush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // The soft light around the play button, the button itself covers the middle.
    static Brush Halo(Color color)
    {
        var brush = new RadialGradientBrush(
        [
            new GradientStop(WithAlpha(color, 0x8C), 0.5),
            new GradientStop(WithAlpha(color, 0x38), 0.72),
            new GradientStop(WithAlpha(color, 0), 1),
        ]);
        brush.Freeze();
        return brush;
    }

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t),
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
