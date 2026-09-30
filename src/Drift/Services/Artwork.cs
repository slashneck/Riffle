using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drift.Audio;
using Drift.Radio;

namespace Drift.Services;

public sealed record Artwork(BitmapSource Image, BitmapSource Thumb, byte[] Bytes, Palette Palette);

public sealed class ArtworkService
{
    const int MaxImageBytes = 4 * 1024 * 1024;
    const int CacheSize = 40;

    readonly Dictionary<string, Artwork> images = [];
    readonly LinkedList<string> recent = new();
    readonly Dictionary<string, string?> lookups = [];
    readonly Dictionary<string, Artwork> placeholders = [];
    Task<Dictionary<string, string>>? ntsArt;

    public bool FindCovers { get; set; } = true;

    public async Task<Artwork> ResolveAsync(Station station, TrackInfo? track, CancellationToken token)
    {
        try
        {
            var url = await CoverUrlAsync(station, track, token) ?? await StationArtUrlAsync(station, token);
            if (url is not null && await LoadAsync(url, token) is { } art)
                return art;
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
        }
        return Placeholder(station);
    }

    async Task<Uri?> CoverUrlAsync(Station station, TrackInfo? track, CancellationToken token)
    {
        if (track is null || track.IsAdvert)
            return null;

        if (station.Art == "stream")
            return Net.SafeImageUri(track.ArtUrl);

        if (station.Art == "lookup" && FindCovers && track.Artist is { } artist && track.Title is { } title)
        {
            if (Net.SafeImageUri(await LookupAsync(artist, title, token)) is { } cover)
                return cover;
        }

        // YouTube tracks carry their thumbnail, which beats a generic picture
        return track.VideoId is not null ? Net.SafeImageUri(track.ArtUrl) : null;
    }

    async Task<Uri?> StationArtUrlAsync(Station station, CancellationToken token)
    {
        if (station.Art == "none")
            return null;

        if (station.Art.StartsWith("nts:", StringComparison.Ordinal))
        {
            ntsArt ??= FetchNtsArtAsync();
            var map = await ntsArt.WaitAsync(token);
            if (map.TryGetValue(station.Art[4..], out var url))
                return Net.SafeImageUri(url);
            if (map.Count == 0)
                ntsArt = null;
        }
        return Net.SafeImageUri(station.Logo);
    }

    // iTunes search only ever sees the artist and title the station is broadcasting.
    async Task<string?> LookupAsync(string artist, string title, CancellationToken token)
    {
        var key = $"{artist}\n{title}".ToLowerInvariant();
        if (lookups.TryGetValue(key, out var cached))
            return cached;

        string? found = await SearchAsync(artist, title, token);
        var plain = StripExtras(title);
        if (found is null && plain != title)
            found = await SearchAsync(artist, plain, token);

        lookups[key] = found;
        return found;
    }

    static async Task<string?> SearchAsync(string artist, string title, CancellationToken token)
    {
        var term = Uri.EscapeDataString($"{artist} {title}");
        var url = $"https://itunes.apple.com/search?term={term}&media=music&entity=song&limit=6";

        using var response = await Net.Client.GetAsync(url, token);
        if (!response.IsSuccessStatusCode)
            return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var wanted = Simplify(artist);
        foreach (var item in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            var name = item.TryGetProperty("artistName", out var n) ? Simplify(n.GetString() ?? "") : "";
            if (name.Length == 0 || !(name.Contains(wanted) || wanted.Contains(name)))
                continue;

            if (item.TryGetProperty("artworkUrl100", out var art) && art.GetString() is { } small)
                return small.Replace("100x100bb", "600x600bb", StringComparison.Ordinal);
        }
        return null;
    }

    static string StripExtras(string title)
    {
        int cut = title.IndexOfAny(['(', '[']);
        return cut > 2 ? title[..cut].Trim() : title;
    }

    static string Simplify(string text) =>
        new string(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    static async Task<Dictionary<string, string>> FetchNtsArtAsync()
    {
        var map = new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(await Net.Client.GetStringAsync("https://www.nts.live/api/v2/mixtapes"));
            foreach (var mix in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                var alias = mix.GetProperty("mixtape_alias").GetString();
                if (alias is not null &&
                    mix.TryGetProperty("media", out var media) &&
                    media.TryGetProperty("picture_medium_large", out var picture) &&
                    picture.GetString() is { } url)
                    map[alias] = url;
            }
        }
        catch (Exception)
        {
        }
        return map;
    }

    async Task<Artwork?> LoadAsync(Uri uri, CancellationToken token)
    {
        var key = uri.AbsoluteUri;
        if (images.TryGetValue(key, out var hit))
        {
            recent.Remove(key);
            recent.AddFirst(key);
            return hit;
        }

        using var response = await Net.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode && uri.AbsolutePath.EndsWith("/maxresdefault.jpg", StringComparison.Ordinal))
        {
            // not every video has a full size thumbnail
            return await LoadAsync(new Uri(uri.AbsoluteUri.Replace("/maxresdefault.jpg", "/mqdefault.jpg")), token);
        }
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes)
            return null;

        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
            return null;

        var art = await Task.Run(() => Decode(bytes), token);
        if (art is null)
            return null;

        images[key] = art;
        recent.AddFirst(key);
        while (recent.Count > CacheSize)
        {
            images.Remove(recent.Last!.Value);
            recent.RemoveLast();
        }
        return art;
    }

    static Artwork? Decode(byte[] bytes)
    {
        try
        {
            var image = Square(Bitmap(bytes, 640));
            var thumb = Square(Bitmap(bytes, 128));
            return new Artwork(image, thumb, Encode(image), Palette.FromImage(thumb));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // video thumbnails are 16:9, covers everywhere in Drift are square
    static BitmapSource Square(BitmapSource image)
    {
        int w = image.PixelWidth, h = image.PixelHeight;
        if (Math.Abs(w - h) <= 2)
            return image;

        int side = Math.Min(w, h);
        var cropped = new CroppedBitmap(image, new Int32Rect((w - side) / 2, (h - side) / 2, side, side));
        cropped.Freeze();
        return cropped;
    }

    static byte[] Encode(BitmapSource image)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    static BitmapImage Bitmap(byte[] bytes, int width)
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

    public Artwork Placeholder(Station station)
    {
        if (placeholders.TryGetValue(station.Id, out var art))
            return art;

        art = Paint(station.TintColor, station.Id.GetHashCode());
        placeholders[station.Id] = art;
        return art;
    }

    public static Artwork Paint(Color tint, int seed)
    {
        const int size = 480;
        var random = new Random(seed);
        var (h, s, _) = Palette.ToHsl(tint);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = new LinearGradientBrush(
                Palette.FromHsl(h, Math.Min(s, 0.5), 0.2),
                Palette.FromHsl((h + 30) % 360, Math.Min(s, 0.45), 0.08),
                new Point(0, 0), new Point(1, 1));
            dc.DrawRectangle(bg, null, new Rect(0, 0, size, size));

            for (int i = 0; i < 3; i++)
            {
                var center = new Point(size * (0.25 + random.NextDouble() * 0.5), size * (0.3 + random.NextDouble() * 0.5));
                double radius = size * (0.28 + random.NextDouble() * 0.25);
                var glow = new RadialGradientBrush(
                    Palette.FromHsl((h + i * 18) % 360, Math.Clamp(s, 0.3, 0.7), 0.62, 110),
                    Palette.FromHsl(h, s, 0.5, 0));
                dc.DrawEllipse(glow, null, center, radius, radius);
            }

            var ring = new Pen(new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)), 1.5);
            for (int i = 1; i <= 4; i++)
                dc.DrawEllipse(null, ring, new Point(size / 2.0, size * 0.62), size * 0.1 * i, size * 0.1 * i);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);

        var thumb = new TransformedBitmap(bitmap, new ScaleTransform(0.2, 0.2));
        thumb.Freeze();
        return new Artwork(bitmap, thumb, stream.ToArray(), Palette.FromTint(tint));
    }
}
