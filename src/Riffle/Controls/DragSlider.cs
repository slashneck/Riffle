using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Riffle.Controls;

// Press anywhere on the bar and it jumps there, then follows the mouse for as long as
// the button is held, even off the bar. The scroll wheel nudges it too.
public sealed class DragSlider : Slider
{
    Track? track;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        track = GetTemplateChild("PART_Track") as Track;
    }

    void MoveTo(MouseEventArgs e)
    {
        if (track is null)
            return;

        // Worked out from the mouse and the bar alone. The track's own ValueFromPoint goes
        // by where the knob last was, which jumps around while the value is changing.
        var at = e.GetPosition(track);
        bool vertical = Orientation == Orientation.Vertical;
        double knob = track.Thumb is { } thumb ? (vertical ? thumb.ActualHeight : thumb.ActualWidth) : 0;
        double length = (vertical ? track.ActualHeight : track.ActualWidth) - knob;
        if (length <= 0)
            return;

        double along = ((vertical ? track.ActualHeight - at.Y : at.X) - knob / 2) / length;
        Value = Minimum + Math.Clamp(along, 0, 1) * (Maximum - Minimum);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        MoveTo(e);
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured)
            MoveTo(e);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        Value = Math.Clamp(Value + (e.Delta > 0 ? 1 : -1) * (Maximum - Minimum) / 25, Minimum, Maximum);
        e.Handled = true;
    }
}
