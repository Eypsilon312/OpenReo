using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenReo.Core.Layouts;
using OpenReo.Core.Models;
using OpenReo.Core.Localization;

namespace OpenReo.App.Dialogs;

/// <summary>Editor for user defined layouts (grid size + merged tiles).</summary>
public partial class LayoutEditorDialog : Window
{
    private readonly LayoutManager _layouts;
    private LayoutDefinition _current;
    private bool _suppress;
    private (int r, int c)? _selStart;
    private (int r, int c) _selEnd;
    private bool _selecting;

    /// <summary>Set when the user chose "save &amp; apply".</summary>
    public LayoutDefinition? LayoutToApply { get; private set; }

    public LayoutEditorDialog(LayoutManager layouts)
    {
        InitializeComponent();
        _layouts = layouts;
        for (int i = 1; i <= 8; i++) { RowsBox.Items.Add(i); ColsBox.Items.Add(i); }
        _current = NewLayout();
        RefreshList();
        LoadIntoEditor(_current);
    }

    private LayoutDefinition NewLayout()
    {
        var l = LayoutDefinition.Grid(Guid.NewGuid().ToString("N"), Lang.F("Eigenes Layout {0}", _layouts.Custom.Count + 1), 3, 4);
        return l;
    }

    private void RefreshList(string? selectId = null)
    {
        LayoutList.ItemsSource = null;
        LayoutList.ItemsSource = _layouts.Custom.ToList();
        if (selectId != null) LayoutList.SelectedItem = _layouts.Custom.FirstOrDefault(l => l.Id == selectId);
        DeleteButton.IsEnabled = _layouts.Custom.Count > 0;
    }

    private void LoadIntoEditor(LayoutDefinition l)
    {
        _current = l.Clone();
        _suppress = true;
        NameBox.Text = _current.Name;
        RowsBox.SelectedItem = _current.Rows;
        ColsBox.SelectedItem = _current.Columns;
        _suppress = false;
        _selStart = null;
        Render();
    }

    private void OnLayoutSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LayoutList.SelectedItem is LayoutDefinition l) LoadIntoEditor(l);
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        LayoutList.SelectedItem = null;
        LoadIntoEditor(NewLayout());
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (LayoutList.SelectedItem is not LayoutDefinition l) return;
        if (MessageBox.Show(this, Lang.F("Layout „{0}“ löschen?", l.Name), "OpenReo", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _layouts.DeleteCustomLayout(l.Id);
        RefreshList();
        LoadIntoEditor(NewLayout());
    }

    private void OnGridSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || RowsBox.SelectedItem is not int rows || ColsBox.SelectedItem is not int cols) return;
        _current.Rows = rows;
        _current.Columns = cols;
        _current.Normalize();
        SortCells();
        _selStart = null;
        Render();
    }

    private void SortCells() => _current.Cells = _current.Cells.OrderBy(c => c.Row).ThenBy(c => c.Column).ToList();

    // ---------------------------------------------------------------- preview

    private void Render()
    {
        PreviewGrid.Children.Clear();
        PreviewGrid.RowDefinitions.Clear();
        PreviewGrid.ColumnDefinitions.Clear();
        SelectionGrid.RowDefinitions.Clear();
        SelectionGrid.ColumnDefinitions.Clear();
        SelectionGrid.Children.Clear();
        for (int r = 0; r < _current.Rows; r++)
        {
            PreviewGrid.RowDefinitions.Add(new RowDefinition());
            SelectionGrid.RowDefinitions.Add(new RowDefinition());
        }
        for (int c = 0; c < _current.Columns; c++)
        {
            PreviewGrid.ColumnDefinitions.Add(new ColumnDefinition());
            SelectionGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (int i = 0; i < _current.Cells.Count; i++)
        {
            var cell = _current.Cells[i];
            var b = new Border
            {
                Background = (Brush)FindResource("Panel2Brush"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(3),
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("TextDimBrush"),
                    FontSize = cell.RowSpan > 1 ? 22 : 14,
                },
            };
            Grid.SetRow(b, cell.Row);
            Grid.SetColumn(b, cell.Column);
            Grid.SetRowSpan(b, cell.RowSpan);
            Grid.SetColumnSpan(b, cell.ColumnSpan);
            PreviewGrid.Children.Add(b);
        }
        if (_selStart is { } s)
        {
            var (r0, c0, r1, c1) = SelectionRect(s, _selEnd);
            var sel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x50, 0x2F, 0x8C, 0xFF)),
                BorderBrush = (Brush)FindResource("AccentBrush"),
                BorderThickness = new Thickness(2),
                Margin = new Thickness(1),
            };
            Grid.SetRow(sel, r0);
            Grid.SetColumn(sel, c0);
            Grid.SetRowSpan(sel, r1 - r0 + 1);
            Grid.SetColumnSpan(sel, c1 - c0 + 1);
            SelectionGrid.Children.Add(sel);
        }
        InfoText.Text = Lang.F("{0} Kacheln", _current.TileCount);
    }

    private static (int r0, int c0, int r1, int c1) SelectionRect((int r, int c) a, (int r, int c) b) =>
        (Math.Min(a.r, b.r), Math.Min(a.c, b.c), Math.Max(a.r, b.r), Math.Max(a.c, b.c));

    private (int r, int c) CellAt(Point p)
    {
        int c = (int)(p.X / Math.Max(1, PreviewHost.ActualWidth) * _current.Columns);
        int r = (int)(p.Y / Math.Max(1, PreviewHost.ActualHeight) * _current.Rows);
        return (Math.Clamp(r, 0, _current.Rows - 1), Math.Clamp(c, 0, _current.Columns - 1));
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _selStart = CellAt(e.GetPosition(PreviewHost));
        _selEnd = _selStart.Value;
        _selecting = true;
        PreviewHost.CaptureMouse();
        Render();
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_selecting) return;
        var cell = CellAt(e.GetPosition(PreviewHost));
        if (cell != _selEnd) { _selEnd = cell; Render(); }
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _selecting = false;
        PreviewHost.ReleaseMouseCapture();
    }

    // ---------------------------------------------------------------- operations

    private void OnMergeClick(object sender, RoutedEventArgs e)
    {
        if (_selStart is not { } s) return;
        var (r0, c0, r1, c1) = SelectionRect(s, _selEnd);

        // Grow the rectangle until no tile sticks out of it.
        bool grown;
        do
        {
            grown = false;
            foreach (var cell in _current.Cells)
            {
                bool intersects = cell.Row <= r1 && cell.Row + cell.RowSpan - 1 >= r0 && cell.Column <= c1 && cell.Column + cell.ColumnSpan - 1 >= c0;
                if (!intersects) continue;
                int nr0 = Math.Min(r0, cell.Row), nc0 = Math.Min(c0, cell.Column);
                int nr1 = Math.Max(r1, cell.Row + cell.RowSpan - 1), nc1 = Math.Max(c1, cell.Column + cell.ColumnSpan - 1);
                if (nr0 != r0 || nc0 != c0 || nr1 != r1 || nc1 != c1) { r0 = nr0; c0 = nc0; r1 = nr1; c1 = nc1; grown = true; }
            }
        } while (grown);

        _current.Cells.RemoveAll(cell => cell.Row >= r0 && cell.Row <= r1 && cell.Column >= c0 && cell.Column <= c1);
        _current.Cells.Add(new LayoutCell(r0, c0, r1 - r0 + 1, c1 - c0 + 1));
        _current.Normalize();
        SortCells();
        _selStart = null;
        Render();
    }

    private void OnSplitClick(object sender, RoutedEventArgs e)
    {
        if (_selStart is not { } s) return;
        var (r0, c0, r1, c1) = SelectionRect(s, _selEnd);
        _current.Cells.RemoveAll(cell => (cell.RowSpan > 1 || cell.ColumnSpan > 1) &&
            cell.Row <= r1 && cell.Row + cell.RowSpan - 1 >= r0 && cell.Column <= c1 && cell.Column + cell.ColumnSpan - 1 >= c0);
        _current.Normalize();
        SortCells();
        _selStart = null;
        Render();
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _current.Cells.Clear();
        _current.Normalize();
        SortCells();
        _selStart = null;
        Render();
    }

    private LayoutDefinition? Save()
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { InfoText.Text = Lang.T("Bitte einen Namen eingeben."); return null; }
        _current.Name = name;
        var saved = _current.Clone();
        _layouts.SaveCustomLayout(saved);
        RefreshList(saved.Id);
        InfoText.Text = Lang.F("„{0}“ gespeichert.", name);
        return saved;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => Save();

    private void OnSaveApplyClick(object sender, RoutedEventArgs e)
    {
        var saved = Save();
        if (saved == null) return;
        LayoutToApply = _layouts.Find(saved.Id);
        DialogResult = true;
    }
}
