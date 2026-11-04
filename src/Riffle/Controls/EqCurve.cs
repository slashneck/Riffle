using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Riffle.Core;
using Riffle.Themes;

namespace Riffle.Controls;

// A real EQ graph: the music's spectrum behind, the curve you're actually applying on top.
// Press anywhere and the nearest band follows the mouse. Up and down is gain, sideways is
// frequency, the scroll wheel is how wide the band is, double click puts it back at zero.
public sealed class EqCurve : FrameworkElement
{
    const double MinHz = 30, MaxHz = 16000;
    const double Range = 14;
    const double SampleRate = 48000;
    const int Points = 150;

    static readonly double[] GridHz = [50, 100, 200, 500, 1000, 2000, 5000, 10000];
    static readonly Typeface Face = new("Segoe UI");
    static readonly Brush GridBrush = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    static readonly Brush ZeroBrush = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    static readonly Brush LabelBrush = Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
    static readonly Brush SpectrumBrush = Frozen(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
    static readonly Brush Panel = Frozen(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
    static readonly Brush KnobCore = Frozen(Color.FromRgb(0x0B, 0x0C, 0x0F));

    List<EqBand> bands = [];
    readonly float[] shown = new float[24];
    int active = -1;
    int hovered = -1;
    bool dragging;
    bool hooked;

    public EqCurve()
    {
        Cursor = Cursors.Hand;
        ClipToBounds = true;
        IsVisibleChanged += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
    }

    public event Action<int, EqBand>? BandChanged;

    public static Func<float[]>? Levels { get; set; }

    public void SetBands(List<EqBand> values)
    {
        bands = values;
        InvalidateVisual();
    }

    void Hook()
    {
        if (IsVisible && !hooked)
        {
            CompositionTarget.Rendering += OnFrame;
            hooked = true;
        }
        else if (!IsVisible)
        {
            Unhook();
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
        var levels = Levels?.Invoke();
        if (levels is null)
            return;

        bool moved = false;
        for (int i = 0; i < shown.Length && i < levels.Length; i++)
        {
            float next = shown[i] + (levels[i] - shown[i]) * (levels[i] > shown[i] ? 0.35f : 0.1f);
            if (Math.Abs(next - shown[i]) > 0.002f)
                moved = true;
            shown[i] = next;
        }
        if (moved)
            InvalidateVisual();
    }

    double XOf(double hz) => Math.Log(hz / MinHz) / Math.Log(MaxHz / MinHz) * ActualWidth;
    double HzOf(double x) => MinHz * Math.Pow(MaxHz / MinHz, Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1));
    double YOf(double db) => ActualHeight / 2 - db / Range * (ActualHeight / 2 - 12);
    double DbOf(double y) => Math.Clamp((ActualHeight / 2 - y) / (ActualHeight / 2 - 12) * Range, -12, 12);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        dc.DrawRoundedRectangle(Panel, null, new Rect(0, 0, w, h), 12, 12);

        var grid = new Pen(GridBrush, 1);
        foreach (var hz in GridHz)
            dc.DrawLine(grid, new Point(XOf(hz), 0), new Point(XOf(hz), h));
        foreach (var db in new[] { -12, -6, 6, 12 })
            dc.DrawLine(grid, new Point(0, YOf(db)), new Point(w, YOf(db)));
        dc.DrawLine(new Pen(ZeroBrush, 1), new Point(0, YOf(0)), new Point(w, YOf(0)));

        DrawSpectrum(dc, w, h);
        DrawCurve(dc, w);

        foreach (var (hz, text) in new[] { (100.0, "100"), (1000.0, "1k"), (10000.0, "10k") })
            Label(dc, text, XOf(hz) + 4, h - 16);

        for (int i = 0; i < bands.Count; i++)
        {
            var center = new Point(XOf(bands[i].Freq), YOf(bands[i].Gain));
            bool lit = i == active || i == hovered;
            dc.DrawEllipse(Theme.Accent, null, center, lit ? 8 : 6, lit ? 8 : 6);
            dc.DrawEllipse(KnobCore, null, center, lit ? 3.5 : 2.5, lit ? 3.5 : 2.5);
        }

        int readout = active >= 0 ? active : hovered;
        if (readout >= 0 && readout < bands.Count)
        {
            var band = bands[readout];
            var hzText = band.Freq >= 1000 ? $"{band.Freq / 1000:0.#} kHz" : $"{band.Freq:0} Hz";
            Label(dc, $"{hzText}   {band.Gain:+0.0;-0.0;0.0} dB", 10, 7, Brushes.White);
        }
    }

    void DrawSpectrum(DrawingContext dc, double w, double h)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(0, h), true, true);
            for (int i = 0; i < shown.Length; i++)
            {
                double hz = 50 * Math.Pow(14000 / 50.0, (i + 0.5) / shown.Length);
                g.LineTo(new Point(XOf(hz), h - shown[i] * h * 0.85), false, false);
            }
            g.LineTo(new Point(w, h), false, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(SpectrumBrush, null, geometry);
    }

    void DrawCurve(DrawingContext dc, double w)
    {
        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        double zero = YOf(0);
        using (var l = line.Open())
        using (var f = fill.Open())
        {
            f.BeginFigure(new Point(0, zero), true, true);
            for (int i = 0; i <= Points; i++)
            {
                double x = w * i / Points;
                var p = new Point(x, YOf(Math.Clamp(Response(HzOf(x)), -Range, Range)));
                if (i == 0)
                    l.BeginFigure(p, false, false);
                else
                    l.LineTo(p, true, false);
                f.LineTo(p, false, false);
            }
            f.LineTo(new Point(w, zero), false, false);
        }
        line.Freeze();
        fill.Freeze();

        dc.PushOpacity(0.16);
        dc.DrawGeometry(Theme.Accent, null, fill);
        dc.Pop();
        dc.DrawGeometry(null, new Pen(Theme.Accent, 2) { LineJoin = PenLineJoin.Round }, line);
    }

    void Label(DrawingContext dc, string text, double x, double y, Brush? brush = null)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10.5,
            brush ?? LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(x, y));
    }

    // The same filters the audio goes through, so what you see is what you get.
    double Response(double hz)
    {
        double total = 0;
        foreach (var band in bands)
            total += BandDb(band, hz);
        return total;
    }

    static double BandDb(EqBand band, double hz)
    {
        if (Math.Abs(band.Gain) < 0.01)
            return 0;

        double w0 = 2 * Math.PI * band.Freq / SampleRate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double a = Math.Pow(10, band.Gain / 40);
        double b0, b1, b2, a0, a1, a2;

        if (band.Type == "peaking")
        {
            double alpha = sin / (2 * Math.Max(0.1, band.Q));
            b0 = 1 + alpha * a; b1 = -2 * cos; b2 = 1 - alpha * a;
            a0 = 1 + alpha / a; a1 = -2 * cos; a2 = 1 - alpha / a;
        }
        else
        {
            double beta = 2 * Math.Sqrt(a) * sin / Math.Sqrt(2);
            if (band.Type == "lowshelf")
            {
                b0 = a * (a + 1 - (a - 1) * cos + beta);
                b1 = 2 * a * (a - 1 - (a + 1) * cos);
                b2 = a * (a + 1 - (a - 1) * cos - beta);
                a0 = a + 1 + (a - 1) * cos + beta;
                a1 = -2 * (a - 1 + (a + 1) * cos);
                a2 = a + 1 + (a - 1) * cos - beta;
            }
            else
            {
                b0 = a * (a + 1 + (a - 1) * cos + beta);
                b1 = -2 * a * (a - 1 + (a + 1) * cos);
                b2 = a * (a + 1 + (a - 1) * cos - beta);
                a0 = a + 1 - (a - 1) * cos + beta;
                a1 = 2 * (a - 1 - (a + 1) * cos);
                a2 = a + 1 - (a - 1) * cos - beta;
            }
        }

        double w = 2 * Math.PI * hz / SampleRate;
        double c1 = Math.Cos(w), s1 = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
        double numRe = b0 + b1 * c1 + b2 * c2, numIm = -(b1 * s1 + b2 * s2);
        double denRe = a0 + a1 * c1 + a2 * c2, denIm = -(a1 * s1 + a2 * s2);
        double power = (numRe * numRe + numIm * numIm) / Math.Max(1e-12, denRe * denRe + denIm * denIm);
        return 10 * Math.Log10(Math.Max(1e-9, power));
    }

    int Nearest(Point p)
    {
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < bands.Count; i++)
        {
            double dx = XOf(bands[i].Freq) - p.X;
            double dy = (YOf(bands[i].Gain) - p.Y) * 0.35;
            double distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    void Apply(Point p)
    {
        if (active < 0 || active >= bands.Count)
            return;
        var band = bands[active];
        band.Freq = Math.Clamp(Math.Round(HzOf(p.X)), band.MinFreq, band.MaxFreq);
        band.Gain = Math.Round(DbOf(p.Y) * 2) / 2;
        BandChanged?.Invoke(active, band);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        active = Nearest(e.GetPosition(this));
        if (active < 0)
            return;

        if (e.ClickCount == 2)
        {
            bands[active].Gain = 0;
            BandChanged?.Invoke(active, bands[active]);
            active = -1;
            InvalidateVisual();
            return;
        }

        dragging = CaptureMouse();
        Apply(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (dragging)
        {
            Apply(p);
            return;
        }
        int near = Nearest(p);
        if (near != hovered)
        {
            hovered = near;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (dragging)
        {
            dragging = false;
            active = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (dragging)
            return;
        hovered = -1;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        int target = active >= 0 ? active : hovered;
        if (target < 0 || target >= bands.Count || bands[target].Type != "peaking")
            return;
        bands[target].Q = Math.Clamp(bands[target].Q * (e.Delta > 0 ? 1.15 : 0.87), 0.3, 6);
        BandChanged?.Invoke(target, bands[target]);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Theme.StaticPropertyChanged += (_, change) => { if (change.PropertyName == nameof(Theme.Accent)) InvalidateVisual(); };
    }

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
