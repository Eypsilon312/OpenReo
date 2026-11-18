using System.Windows;
using System.Windows.Controls;

namespace OpenReo.App.Dialogs;

/// <summary>Simple single line text input dialog.</summary>
public static class InputDialog
{
    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.Resources["AccentButton"] };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = title,
            Owner = owner,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            Style = (Style)Application.Current.Resources["DialogWindow"],
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel,
        };
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? box.Text : null;
    }
}
