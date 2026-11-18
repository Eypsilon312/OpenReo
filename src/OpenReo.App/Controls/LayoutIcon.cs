using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenReo.Core.Models;

namespace OpenReo.App.Controls;

/// <summary>Draws a small preview of a layout (used in toolbar buttons and menus).</summary>
public static class LayoutIcon
{
    public static FrameworkElement Create(LayoutDefinition layout, double width = 18, double height = 13)
    {
        var grid = new Grid { Width = width, Height = height, SnapsToDevicePixels = true };
        for (int r = 0; r < layout.Rows; r++) grid.RowDefinitions.Add(new RowDefinition());
        for (int c = 0; c < layout.Columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var cell in layout.Cells)
        {
            var b = new Border
            {
                BorderThickness = new Thickness(0.8),
                Margin = new Thickness(0.6),
                CornerRadius = new CornerRadius(1),
            };
            b.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("Foreground")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1),
            });
            Grid.SetRow(b, cell.Row);
            Grid.SetColumn(b, cell.Column);
            Grid.SetRowSpan(b, cell.RowSpan);
            Grid.SetColumnSpan(b, cell.ColumnSpan);
            grid.Children.Add(b);
        }
        return grid;
    }
}
