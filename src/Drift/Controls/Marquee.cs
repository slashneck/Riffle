using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Drift.Controls;

// A single line of text that slides in when it changes and slowly scrolls when it
// doesn't fit, pausing at the start of every pass.
public sealed class Marquee : FrameworkElement
{
    const double Speed = 24;
    const double Spacing = 36;
    const double Hold = 2.8;
    const double SwapTime = 0.42;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Marquee),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) => ((Marquee)d).OnTextChanged((string?)e.OldValue)));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(Marquee),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((Marquee)d).Rebuild()));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(Marquee),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((Marquee)d).Rebuild()));

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight), typeof(FontWeight), typeof(Marquee),
        new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((Marquee)d).Rebuild()));

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily), typeof(FontFamily), typeof(Marquee),
        new FrameworkPropertyMetadata(new FontFamily("Segoe UI"), FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((Marquee)d).Rebuild()));

    FormattedText? current;
    FormattedText? previous;
    double offset;
    double holdLeft = Hold;
    double swap = 1;
    TimeSpan lastFrame;
    bool hooked;

    public Marquee()
    {
        ClipToBounds = true;
        IsVisibleChanged += (_, _) => Wake();
        Unloaded += (_, _) => Unhook();
        SizeChanged += (_, _) => Wake();
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    bool Overflows => current is not null && current.WidthIncludingTrailingWhitespace > ActualWidth + 0.5;

    void OnTextChanged(string? old)
    {
        previous = string.IsNullOrEmpty(old) || !IsLoaded ? null : Build(old);
        current = Build(Text);
        swap = previous is null ? 1 : 0;
        offset = 0;
        holdLeft = Hold;
        Wake();
    }

    void Rebuild()
    {
        current = Build(Text);
        previous = null;
        InvalidateVisual();
    }

    FormattedText? Build(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
            FontSize, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            Trimming = TextTrimming.None,
            MaxLineCount = 1,
        };
    }

    protected override Size MeasureOverride(Size available)
    {
        current ??= Build(Text);
        double height = current?.Height ?? Math.Ceiling(FontSize * 1.35);
        double natural = Math.Ceiling(current?.WidthIncludingTrailingWhitespace ?? 0);
        return new Size(Math.Min(natural, available.Width), height);
    }

    void Wake()
    {
        if (!hooked && IsVisible && (swap < 1 || Overflows))
        {
            lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
            hooked = true;
        }
        InvalidateVisual();
    }

    void Unhook()
    {
        if (!hooked)
            return;
        CompositionTarget.Rendering -= OnFrame;
        hooked = false;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = lastFrame == TimeSpan.Zero ? 0 : Math.Min(0.1, (now - lastFrame).TotalSeconds);
        lastFrame = now;

        if (!IsVisible)
        {
            Unhook();
            return;
        }

        if (swap < 1)
        {
            swap = Math.Min(1, swap + dt / SwapTime);
            if (swap >= 1)
                previous = null;
        }
        else if (Overflows)
        {
            if (holdLeft > 0)
            {
                holdLeft -= dt;
            }
            else
            {
                offset += Speed * dt;
                double loop = current!.WidthIncludingTrailingWhitespace + Spacing;
                if (offset >= loop)
                {
                    offset = 0;
                    holdLeft = Hold;
                }
            }
        }
        else
        {
            offset = 0;
            Unhook();
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        double t = Ease(swap);
        bool overflow = Overflows;

        if (overflow)
        {
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            double edge = Math.Min(0.12, 14 / Math.Max(1, width));
            mask.GradientStops.Add(new GradientStop(offset > 0 || holdLeft <= 0 ? Colors.Transparent : Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, edge));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - edge));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            dc.PushOpacityMask(mask);
        }

        if (previous is not null && t < 1)
        {
            dc.PushOpacity(1 - t);
            dc.DrawText(previous, new Point(0, -7 * t));
            dc.Pop();
        }

        if (current is not null)
        {
            dc.PushOpacity(t);
            double y = 7 * (1 - t);
            dc.DrawText(current, new Point(-offset, y));
            if (overflow)
                dc.DrawText(current, new Point(-offset + current.WidthIncludingTrailingWhitespace + Spacing, y));
            dc.Pop();
        }

        if (overflow)
            dc.Pop();
    }

    static double Ease(double t) => 1 - Math.Pow(1 - t, 3);
}
