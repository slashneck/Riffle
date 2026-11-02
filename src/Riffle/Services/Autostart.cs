using Microsoft.Win32;

namespace Riffle.Services;

// "Start with Windows" is a single value under the current user's Run key, nothing else.
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Riffle";
    public const string Flag = "--background";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value && Environment.ProcessPath is { } exe)
                key.SetValue(Name, $"\"{exe}\" {Flag}");
            else
                key.DeleteValue(Name, false);
        }
    }
}
