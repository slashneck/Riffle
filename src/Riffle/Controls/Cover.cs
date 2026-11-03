using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Riffle.Services;

namespace Riffle.Controls;

// Paints a border with a cover from a url, through Riffle's own client so images stay
// in memory instead of landing in the system's web cache.
public static class Cover
{
    const int Keep = 400;
    static readonly SemaphoreSlim Slots = new(6);
    static readonly Dictionary<string, BitmapImage> Cache = [];
    static readonly Queue<string> Order = new();
    static readonly Dictionary<string, Task<BitmapImage?>> Pending = [];

    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(Cover), new PropertyMetadata(null, OnUrlChanged));

    public static readonly DependencyProperty PixelsProperty = DependencyProperty.RegisterAttached(
        "Pixels", typeof(int), typeof(Cover), new PropertyMetadata(96));

    public static string? GetUrl(DependencyObject d) => (string?)d.GetValue(UrlProperty);
    public static void SetUrl(DependencyObject d, string? value) => d.SetValue(UrlProperty, value);
    public static int GetPixels(DependencyObject d) => (int)d.GetValue(PixelsProperty);
    public static void SetPixels(DependencyObject d, int value) => d.SetValue(PixelsProperty, value);

    static async void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border)
            return;

        var url = (string?)e.NewValue;
        border.Background = (Brush)Application.Current.FindResource("ChipBrush");
        if (url is null)
            return;

        // rows flying past while scrolling shouldn't each start a download
        await Task.Delay(90);
        if (GetUrl(border) != url)
            return;

        int pixels = GetPixels(border);
        var image = await LoadAsync(url, pixels);
        if (image is null || GetUrl(border) != url)
            return;

        var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        border.Background = brush;
    }

    static Task<BitmapImage?> LoadAsync(string url, int pixels)
    {
        var sized = pixels > 200 ? url.Replace("-large.", "-t500x500.", StringComparison.Ordinal) : url;
        var key = sized + "@" + pixels;
        if (Cache.TryGetValue(key, out var hit))
            return Task.FromResult<BitmapImage?>(hit);
        if (Pending.TryGetValue(key, out var running))
            return running;

        var task = FetchAsync(sized, pixels, key);
        Pending[key] = task;
        return task;
    }

    static async Task<BitmapImage?> FetchAsync(string url, int pixels, string key)
    {
        try
        {
            if (Net.SafeMediaUri(url) is not { } uri)
                return null;
            await Slots.WaitAsync();
            BitmapImage image;
            try
            {
                var bytes = await Net.Client.GetByteArrayAsync(uri);
                image = await Task.Run(() => ArtworkService.Bitmap(bytes, pixels));
            }
            finally
            {
                Slots.Release();
            }
            Cache[key] = image;
            Order.Enqueue(key);
            while (Order.Count > Keep)
                Cache.Remove(Order.Dequeue());
            return image;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Pending.Remove(key);
        }
    }
}
