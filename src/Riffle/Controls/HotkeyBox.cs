using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Riffle.Services;

namespace Riffle.Controls;

// Click it, press the keys you want. Backspace clears it, Esc keeps what was there.
public sealed class HotkeyBox : Button
{
    Hotkey? value;
    bool taken;

    public HotkeyBox()
    {
        Focusable = true;
        Show();
    }

    public static event Action<bool>? RecordingChanged;
    public event Action<HotkeyBox, Hotkey?>? Picked;

    public bool IsRecording { get; private set; }

    public void SetValue(Hotkey? key, bool inUse)
    {
        value = key;
        taken = inUse;
        Show();
    }

    protected override void OnClick()
    {
        if (IsRecording)
        {
            Stop();
            return;
        }
        IsRecording = true;
        RecordingChanged?.Invoke(true);
        Focus();
        Show();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsRecording)
        {
            base.OnPreviewKeyDown(e);
            return;
        }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape:
                Stop();
                return;
            case Key.Back or Key.Delete:
                Stop();
                Picked?.Invoke(this, null);
                return;
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin:
                return;
        }

        var modifiers = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
            modifiers |= ModifierKeys.Windows;
        // a plain letter would stop working everywhere else, so those need Ctrl, Alt or Shift.
        // The numpad, F-keys, Pause and Scroll Lock are fine on their own.
        if (modifiers == ModifierKeys.None && !Hotkey.WorksAlone(key))
        {
            Content = "Use the numpad, an F-key, or add Ctrl";
            return;
        }
        Stop();
        Picked?.Invoke(this, new Hotkey(modifiers, key));
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (IsRecording)
            Stop();
    }

    void Stop()
    {
        IsRecording = false;
        RecordingChanged?.Invoke(false);
        Show();
    }

    void Show() => Content = IsRecording ? "Press keys" : value is { } key ? (taken ? key.Label + "  ·  in use" : key.Label) : "Not set";
}
