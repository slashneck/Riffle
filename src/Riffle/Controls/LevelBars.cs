using System.Windows;
using System.Windows.Media;

namespace Riffle.Controls;

public sealed class LevelBars : FrameworkElement
{
    public static Func<float[]>? Source { get; set; }

    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(int), typeof(LevelBars),
        new FrameworkPropertyMetadata(24, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((LevelBars)d).Resize()));

    public static readonly DependencyProperty BarWidthProperty = DependencyProperty.Register(
        nameof(BarWidth), typeof(double), typeof(LevelBars),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(LevelBars),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(
        nameof(Active), typeof(bool), typeof(LevelBars),
        new PropertyMetadata(false, (d, _) => ((LevelBars)d).Wake()));

    public static readonly DependencyProperty CenteredProperty = DependencyProperty.Register(
        nameof(Centered), typeof(bool), typeof(LevelBars),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GainProperty = DependencyProperty.Register(
        nameof(Gain), typeof(double), typeof(LevelBars), new PropertyMetadata(1.0));

    float[] shown = new float[24];

    bool hooked;
    TimeSpan lastFrame;

    public LevelBars()
    {
        IsVisibleChanged += (_, _) => Wake();
        Unloaded += (_, _) => Unhook();
    }

    public int Bars { get => (int)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }
    public double BarWidth { get => (double)GetValue(BarWidthProperty); set => SetValue(BarWidthProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }
    public bool Centered { get => (bool)GetValue(CenteredProperty); set => SetValue(CenteredProperty, value); }
    public double Gain { get => (double)GetValue(GainProperty); set => SetValue(GainProperty, value); }

    void Resize() => shown = new float[Math.Max(1, Bars)];

    void Wake()
    {
        if (IsVisible && !hooked)
        {
            lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
            hooked = true;
        }
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
        if (!IsVisible)
        {
            Unhook();
            return;
        }

        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == lastFrame)
            return;
        float dt = lastFrame == TimeSpan.Zero ? 0.016f : (float)Math.Min(0.1, (now - lastFrame).TotalSeconds);
        lastFrame = now;

        // time based, so motion looks the same at any refresh rate
        float rise = 1 - MathF.Exp(-dt / 0.045f);
        float fall = 1 - MathF.Exp(-dt / 0.16f);

        var levels = Active && Source is not null ? Source() : [];
        int count = shown.Length;
        float gain = (float)Gain;
        bool moving = false;
        bool changed = false;

        for (int i = 0; i < count; i++)
        {
            float target = 0;
            if (levels.Length > 0)
            {
                int from = i * levels.Length / count;
                int to = Math.Max(from + 1, (i + 1) * levels.Length / count);
                // half peak, half average: the peak alone pins wide groups at the top,
                // the average alone barely moves
                float peak = 0, sum = 0;
                for (int k = from; k < to; k++)
                {
                    peak = Math.Max(peak, levels[k]);
                    sum += levels[k];
                }
                target = Math.Min(1f, (peak + sum / (to - from)) * 0.5f * gain);
            }

            float current = shown[i];
            current += (target - current) * (target > current ? rise : fall);
            if (Math.Abs(current - shown[i]) > 0.0015f)
                changed = true;
            if (current > 0.002f)
                moving = true;
            shown[i] = current;
        }

        if (changed)
            InvalidateVisual();
        if (!moving && !Active)
            Unhook();
    }

    protected override void OnRender(DrawingContext dc)
    {
        int count = shown.Length;
        double width = ActualWidth, height = ActualHeight;
        if (count == 0 || width <= 0 || height <= 0)
            return;

        double bar = BarWidth;
        double gap = count > 1 ? (width - bar * count) / (count - 1) : 0;
        var fill = Fill;

        for (int i = 0; i < count; i++)
        {
            double h = Math.Max(bar, shown[i] * height);
            double x = i * (bar + gap);
            double y = Centered ? (height - h) / 2 : height - h;
            dc.DrawRoundedRectangle(fill, null, new Rect(x, y, bar, h), bar / 2, bar / 2);
        }
    }
}
