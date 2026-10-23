using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
namespace MediaFlattener;
// Avalonia has no built-in message box; this mirrors WPF's MessageBox.Show(owner, text, caption).
public static class MessageDialog
{
    public static Task Show(Window owner, string message, string title)
    {
        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
        var dialog = new Window {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White,
            Content = new StackPanel { Margin = new(20), Spacing = 18, Children = {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#19283D")) },
                ok
            } }
        };
        ok.Click += (_, _) => dialog.Close();
        return dialog.ShowDialog(owner);
    }
}
