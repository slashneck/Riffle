using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Riffle.Services;

// A key combination, stored the way it reads: "Ctrl+Alt+Right".
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = ModifierKeys.None;
        foreach (var part in parts[..^1])
        {
            modifiers |= part switch
            {
                "Ctrl" => ModifierKeys.Control,
                "Alt" => ModifierKeys.Alt,
                "Shift" => ModifierKeys.Shift,
                "Win" => ModifierKeys.Windows,
                _ => ModifierKeys.None,
            };
        }
        return Enum.TryParse<Key>(parts[^1], out var key) ? new Hotkey(modifiers, key) : null;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }

    // The same, but the way people read a key: 1 instead of D1.
    public string Label => ToString().Replace("+D", "+").Replace("Oem", "").Replace("Next", "PageDown").Replace("Prior", "PageUp");
}

// Global hotkeys. None are set until someone picks one in Settings.
public sealed class Shortcuts : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_NOREPEAT = 0x4000;

    public static readonly (string Id, string Label)[] Actions =
    [
        ("Show", "Show what's playing"),
        ("Toggle", "Play or pause"),
        ("Next", "Next song"),
        ("Previous", "Previous song"),
    ];

    readonly IntPtr window;
    readonly HwndSource source;
    readonly Settings settings;
    readonly Dictionary<string, Action> run;
    readonly Dictionary<int, string> registered = [];

    public Shortcuts(IntPtr window, Settings settings, Dictionary<string, Action> run)
    {
        this.window = window;
        this.settings = settings;
        this.run = run;
        source = HwndSource.FromHwnd(window);
        source.AddHook(Hook);
        Apply();
    }

    // The ones Windows wouldn't give us, because another app already uses them.
    public HashSet<string> Taken { get; } = [];

    public Hotkey? Get(string id) => settings.Hotkeys.TryGetValue(id, out var text) ? Hotkey.Parse(text) : null;

    public bool Set(string id, Hotkey? key)
    {
        if (key is { } chosen)
            settings.Hotkeys[id] = chosen.ToString();
        else
            settings.Hotkeys.Remove(id);
        settings.Save();
        Apply();
        return !Taken.Contains(id);
    }

    // While a new combination is being picked, the current ones must not fire.
    public void Pause() => Release();

    public void Apply()
    {
        Release();
        Taken.Clear();
        int next = 1;
        foreach (var (id, _) in Actions)
        {
            if (Get(id) is not { } key)
                continue;
            uint modifiers = MOD_NOREPEAT
                | (key.Modifiers.HasFlag(ModifierKeys.Alt) ? 0x1u : 0)
                | (key.Modifiers.HasFlag(ModifierKeys.Control) ? 0x2u : 0)
                | (key.Modifiers.HasFlag(ModifierKeys.Shift) ? 0x4u : 0)
                | (key.Modifiers.HasFlag(ModifierKeys.Windows) ? 0x8u : 0);
            if (RegisterHotKey(window, next, modifiers, (uint)KeyInterop.VirtualKeyFromKey(key.Key)))
                registered[next] = id;
            else
                Taken.Add(id);
            next++;
        }
    }

    void Release()
    {
        foreach (var number in registered.Keys)
            UnregisterHotKey(window, number);
        registered.Clear();
    }

    IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_HOTKEY && registered.TryGetValue(wParam.ToInt32(), out var id) && run.TryGetValue(id, out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Release();
        source.RemoveHook(Hook);
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr window, int id);
}
