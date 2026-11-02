using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Riffle.Controls;

// The song's real waveform as a seek bar. Click or drag anywhere to jump there.
public sealed class WaveformBar : FrameworkElement
{
    const double BarWidth = 2.5;
    const double Gap = 2;

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(WaveformBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayedProperty = DependencyProperty.Register(
        nameof(Played), typeof(Brush), typeof(WaveformBar),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RestProperty = DependencyProperty.Register(
        nameof(Rest), typeof(Brush), typeof(WaveformBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    float[] samples = [];
    double? hover;
    bool dragging;

    public WaveformBar()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Brush Played { get => (Brush)GetValue(PlayedProperty); set => SetValue(PlayedProperty, value); }
    public Brush Rest { get => (Brush)GetValue(RestProperty); set => SetValue(RestProperty, value); }

    public event Action<double>? SeekRequested;
    public event Action<double>? Scrubbing;

    public void SetSamples(float[] values)
    {
        samples = values;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (width <= 0)
            return;

        int bars = (int)((width + Gap) / (BarWidth + Gap));
        double playedTo = (hover ?? Progress) * width;
        double realTo = dragging ? playedTo : Progress * width;

        for (int i = 0; i < bars; i++)
        {
            double level = 0.14;
            if (samples.Length > 0)
            {
                int from = i * samples.Length / bars;
                int to = Math.Max(from + 1, (i + 1) * samples.Length / bars);
                float peak = 0;
                for (int k = from; k < to && k < samples.Length; k++)
                    peak = Math.Max(peak, samples[k]);
                level = Math.Max(0.1, peak);
            }

            double h = Math.Max(BarWidth, level * height);
            double x = i * (BarWidth + Gap);
            var rect = new Rect(x, (height - h) / 2, BarWidth, h);
            bool played = x < realTo;
            bool previewing = hover is not null && x < playedTo;
            dc.PushOpacity(played ? 1 : previewing ? 0.75 : 1);
            dc.DrawRoundedRectangle(played || previewing ? Played : Rest, null, rect, BarWidth / 2, BarWidth / 2);
            dc.Pop();
        }
    }

    double Fraction(MouseEventArgs e) => Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        hover = Fraction(e);
        if (dragging)
            Scrubbing?.Invoke(hover.Value);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (!dragging)
            hover = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        dragging = CaptureMouse();
        hover = Fraction(e);
        Scrubbing?.Invoke(hover.Value);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (dragging)
        {
            dragging = false;
            ReleaseMouseCapture();
            SeekRequested?.Invoke(Fraction(e));
            if (!IsMouseOver)
                hover = null;
            InvalidateVisual();
        }
        e.Handled = true;
    }
}
