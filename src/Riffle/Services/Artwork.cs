using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Riffle.Services;

public sealed record Artwork(BitmapSource Image, BitmapSource Thumb, byte[] Bytes, Palette Palette);

public static class ArtworkService
{
    const int MaxBytes = 4 * 1024 * 1024;
    static readonly Dictionary<string, Artwork> Cache = [];
    static readonly Queue<string> Recent = new();

    public static async Task<Artwork?> LoadAsync(string? url, CancellationToken token)
    {
        if (url is null)
            return null;
        var big = url.Replace("-large.", "-t500x500.", StringComparison.Ordinal);
        if (Net.SafeMediaUri(big) is not { } uri)
            return null;
        if (Cache.TryGetValue(big, out var hit))
            return hit;

        try
        {
            var bytes = await Net.Client.GetByteArrayAsync(uri, token);
            if (bytes.Length > MaxBytes)
                return null;
            var art = await Task.Run(() => Decode(bytes), token);
            if (art is null)
                return null;
            Cache[big] = art;
            Recent.Enqueue(big);
            while (Recent.Count > 30)
                Cache.Remove(Recent.Dequeue());
            return art;
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return null;
        }
    }

    static Artwork? Decode(byte[] bytes)
    {
        try
        {
            var image = Bitmap(bytes, 600);
            var thumb = Bitmap(bytes, 128);
            return new Artwork(image, thumb, bytes, Palette.FromImage(thumb));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static BitmapImage Bitmap(byte[] bytes, int width)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = width;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
    }

    // Used when a song has no cover of its own: the riffle mark on a soft wash of color.
    public static Artwork Placeholder(string seed)
    {
        const int size = 500;
        double hue = seed.Sum(c => (int)c) * 37 % 360;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(
                Palette.FromHsl(hue, 0.28, 0.2), Palette.FromHsl((hue + 40) % 360, 0.25, 0.08), 60), null, new Rect(0, 0, size, size));
            var brush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
            double h = size * 0.05, gap = size * 0.03, top = size / 2.0 - (4 * h + 3 * gap) / 2;
            for (int i = 0; i < 4; i++)
                dc.DrawRoundedRectangle(brush, null, new Rect(i % 2 == 0 ? size * 0.27 : size * 0.37, top + i * (h + gap), size * 0.36, h), h / 2, h / 2);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var thumb = new TransformedBitmap(bitmap, new ScaleTransform(0.26, 0.26));
        thumb.Freeze();
        return new Artwork(bitmap, thumb, stream.ToArray(), Palette.FromHue(hue));
    }

    public static async Task<float[]?> WaveformAsync(string? url, CancellationToken token)
    {
        if (Net.SafeMediaUri(url) is not { } uri)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(await Net.Client.GetStringAsync(uri, token));
            var root = doc.RootElement;
            float height = root.TryGetProperty("height", out var h) ? h.GetSingle() : 140;
            return root.GetProperty("samples").EnumerateArray().Select(v => v.GetSingle() / height).ToArray();
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return null;
        }
    }

    public static async Task<(string? Title, string? Owner, string? Art)> DescribeAsync(string link, CancellationToken token)
    {
        try
        {
            var url = "https://soundcloud.com/oembed?format=json&url=" + Uri.EscapeDataString(link);
            using var doc = JsonDocument.Parse(await Net.Client.GetStringAsync(url, token));
            var root = doc.RootElement;
            string? Read(string name) => root.TryGetProperty(name, out var v) ? v.GetString() : null;
            var title = Read("title");
            var owner = Read("author_name");
            if (title is not null && owner is not null && title.EndsWith(" by " + owner, StringComparison.Ordinal))
                title = title[..^(owner.Length + 4)];
            return (title, owner, Read("thumbnail_url"));
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return (null, null, null);
        }
    }
}
