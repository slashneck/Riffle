using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Riffle.Shell;

namespace Riffle.Views;

// A small question in Riffle's own look, for when the main window isn't around.
public sealed class DarkDialog : Window
{
    bool accepted;

    DarkDialog(string title, string message, string accept, string? cancel)
    {
        Title = "Riffle";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)FindResource("BaseBrush");
        SourceInitialized += (_, _) => Native.SetDwm(new WindowInteropHelper(this).Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        if (cancel is not null)
        {
            var no = new Button { Content = cancel, Style = (Style)FindResource("TextButton"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            no.Click += (_, _) => Close();
            buttons.Children.Add(no);
        }
        var yes = new Button { Content = accept, Style = (Style)FindResource("PrimaryButton"), MinWidth = 130, IsDefault = true };
        yes.Click += (_, _) => { accepted = true; Close(); };
        buttons.Children.Add(yes);

        Content = new StackPanel
        {
            Margin = new Thickness(26, 22, 26, 22),
            Children =
            {
                new TextBlock { Text = title, FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextBrush") },
                new TextBlock { Text = message, FontSize = 12.5, LineHeight = 19, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = (Brush)FindResource("SubtleTextBrush") },
                buttons,
            },
        };
    }

    public static bool Show(string title, string message, string accept, string? cancel)
    {
        var dialog = new DarkDialog(title, message, accept, cancel);
        dialog.ShowDialog();
        return dialog.accepted;
    }
}
