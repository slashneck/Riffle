using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Drift.Services;

// Accent is what the UI tints with, Deep is the dark wash behind everything,
// Glow is a mid tone for shadows and the visualizer.
public readonly record struct Palette(Color Accent, Color Deep, Color Glow)
{
    public static readonly Palette Default = FromTint(Color.FromRgb(0x8F, 0xD3, 0xC1));

    public static Palette FromTint(Color tint)
    {
        var (h, s, _) = ToHsl(tint);
        return Build(h, s);
    }

    public static Palette FromImage(BitmapSource image)
    {
        var small = new FormatConvertedBitmap(Scale(image, 48), PixelFormats.Bgra32, null, 0);
        int width = small.PixelWidth, height = small.PixelHeight;
        var pixels = new byte[width * height * 4];
        small.CopyPixels(pixels, width * 4, 0);

        const int bucketCount = 30;
        var weight = new double[bucketCount];
        var sumR = new double[bucketCount];
        var sumG = new double[bucketCount];
        var sumB = new double[bucketCount];
        double vivid = 0;
        int counted = 0;

        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 128)
                continue;

            double b = pixels[i] / 255.0, g = pixels[i + 1] / 255.0, r = pixels[i + 2] / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double sat = max <= 0 ? 0 : (max - min) / max;
            counted++;

            if (max < 0.14 || sat < 0.2)
                continue;

            double hue = Hue(r, g, b, max, min);
            int bucket = (int)(hue / 360.0 * bucketCount) % bucketCount;
            double w = sat * sat * (0.35 + max);
            weight[bucket] += w;
            sumR[bucket] += r * w;
            sumG[bucket] += g * w;
            sumB[bucket] += b * w;
            vivid += w;
        }

        // mostly black and white artwork: stay neutral instead of amplifying noise
        if (counted == 0 || vivid / counted < 0.02)
            return Build(215, 0.12);

        int best = 0;
        double bestScore = -1;
        for (int k = 0; k < bucketCount; k++)
        {
            double score = weight[k] + 0.5 * (weight[(k + bucketCount - 1) % bucketCount] + weight[(k + 1) % bucketCount]);
            if (score > bestScore)
            {
                bestScore = score;
                best = k;
            }
        }

        double wsum = weight[best];
        var color = Color.FromRgb(
            (byte)(sumR[best] / wsum * 255),
            (byte)(sumG[best] / wsum * 255),
            (byte)(sumB[best] / wsum * 255));
        var (hh, ss, _) = ToHsl(color);
        return Build(hh, ss);
    }

    static Palette Build(double hue, double sat)
    {
        var accent = FromHsl(hue, Math.Clamp(sat, 0.12, 0.72), 0.72);
        var deep = FromHsl(hue, Math.Clamp(sat * 0.55, 0.08, 0.42), 0.12);
        var glow = FromHsl(hue, Math.Clamp(sat, 0.18, 0.8), 0.52);
        return new Palette(accent, deep, glow);
    }

    static BitmapSource Scale(BitmapSource source, int size)
    {
        double factor = (double)size / Math.Max(source.PixelWidth, source.PixelHeight);
        if (factor >= 1)
            return source;
        var scaled = new TransformedBitmap(source, new ScaleTransform(factor, factor));
        scaled.Freeze();
        return scaled;
    }

    static double Hue(double r, double g, double b, double max, double min)
    {
        double d = max - min;
        if (d <= 0)
            return 0;
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        double d = max - min;
        double s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        return (Hue(r, g, b, max, min), s, l);
    }

    public static Color FromHsl(double h, double s, double l, byte alpha = 255)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromArgb(alpha,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
