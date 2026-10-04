// ConfirmDialog.cs — a small Yes/No dialog (Avalonia has no built-in message box)
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace SWBF_C_build;

public class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string yesText, string noText)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RequestedThemeVariant = ThemeVariant.Dark;

        var yes = new Button
        {
            Content = yesText,
            Background = Brushes.White,
            Foreground = Brushes.Black,
            Padding = new Thickness(16, 8),
            IsDefault = true
        };
        var no = new Button
        {
            Content = noText,
            Padding = new Thickness(16, 8),
            IsCancel = true
        };
        yes.Click += (_, _) => Close(true);
        no.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { no, yes }
                }
            }
        };
    }

    /// <summary>Shows the dialog over <paramref name="owner"/>. Closing it with the X counts as "no".</summary>
    public static Task<bool> AskAsync(Window owner, string title, string message,
        string yesText = "Yes", string noText = "No")
    {
        var dialog = new ConfirmDialog(title, message, yesText, noText) { Icon = owner.Icon };
        return dialog.ShowDialog<bool>(owner);
    }
}