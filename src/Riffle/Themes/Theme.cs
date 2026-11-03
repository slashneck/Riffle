using System.Windows.Media;
using Riffle.Services;

namespace Riffle.Themes;

// The accent colors follow the cover art. These brushes are shared and change in place,
// so everything bound to them drifts to the new colors together. They're bound directly
// and stay out of resource dictionaries, because WPF freezes what goes in there.
//
// The blend sets the colors itself, once per frame. A ColorAnimation on these brushes
// crashed WPF's render thread every so often when the song changed.
public static class Theme
{
    const double BlendMs = 1600;

    public static SolidColorBrush Accent { get; } = new(Palette.Default.Accent);
    public static SolidColorBrush AccentSoft { get; } = new(WithAlpha(Palette.Default.Accent, 0x30));
    public static SolidColorBrush AccentFaint { get; } = new(WithAlpha(Palette.Default.Accent, 0x16));
    public static SolidColorBrush Deep { get; } = new(Palette.Default.Deep);
    public static SolidColorBrush Glow { get; } = new(Palette.Default.Glow);

    static readonly SolidColorBrush[] Brushes = [Accent, AccentSoft, AccentFaint, Deep, Glow];
    static readonly Color[] From = new Color[Brushes.Length];
    static readonly Color[] To = new Color[Brushes.Length];
    static long began;
    static bool blending;

    public static Palette Current { get; private set; } = Palette.Default;

    public static void Apply(Palette palette)
    {
        if (palette == Current)
            return;
        Current = palette;

        To[0] = palette.Accent;
        To[1] = WithAlpha(palette.Accent, 0x30);
        To[2] = WithAlpha(palette.Accent, 0x16);
        To[3] = palette.Deep;
        To[4] = palette.Glow;
        for (int i = 0; i < Brushes.Length; i++)
            From[i] = Brushes[i].Color;

        began = Environment.TickCount64;
        if (!blending)
        {
            blending = true;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    static void OnFrame(object? sender, EventArgs e)
    {
        double t = Math.Min(1, (Environment.TickCount64 - began) / BlendMs);
        double eased = (1 - Math.Cos(t * Math.PI)) / 2;
        for (int i = 0; i < Brushes.Length; i++)
            Brushes[i].Color = Mix(From[i], To[i], eased);

        if (t >= 1)
        {
            blending = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t),
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
