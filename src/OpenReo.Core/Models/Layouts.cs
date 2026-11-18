using System.Text.Json.Serialization;

namespace OpenReo.Core.Models;

/// <summary>One tile of a layout grid. A tile may span several rows / columns.</summary>
public sealed class LayoutCell
{
    public int Row { get; set; }
    public int Column { get; set; }
    public int RowSpan { get; set; } = 1;
    public int ColumnSpan { get; set; } = 1;

    public LayoutCell() { }
    public LayoutCell(int row, int col, int rowSpan = 1, int colSpan = 1)
    {
        Row = row; Column = col; RowSpan = rowSpan; ColumnSpan = colSpan;
    }

    public bool Covers(int row, int col) =>
        row >= Row && row < Row + RowSpan && col >= Column && col < Column + ColumnSpan;

    public LayoutCell Clone() => new(Row, Column, RowSpan, ColumnSpan);
}

/// <summary>
/// A multiview layout: a grid with <see cref="Rows"/> x <see cref="Columns"/> base cells
/// and a list of (possibly merged) tiles. The tile order defines the slot index.
/// </summary>
public sealed class LayoutDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Layout";
    public int Rows { get; set; } = 1;
    public int Columns { get; set; } = 1;
    public List<LayoutCell> Cells { get; set; } = new();

    [JsonIgnore]
    public bool IsBuiltIn { get; set; }

    [JsonIgnore]
    public int TileCount => Cells.Count;

    /// <summary>Uniform grid with rows x columns tiles.</summary>
    public static LayoutDefinition Grid(string id, string name, int rows, int cols)
    {
        var l = new LayoutDefinition { Id = id, Name = name, Rows = rows, Columns = cols };
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                l.Cells.Add(new LayoutCell(r, c));
        return l;
    }

    /// <summary>Grid with one large tile (slot 0) and the remaining cells filled with 1x1 tiles.</summary>
    public static LayoutDefinition WithLargeTile(string id, string name, int rows, int cols, int bigRow, int bigCol, int span)
    {
        var l = new LayoutDefinition { Id = id, Name = name, Rows = rows, Columns = cols };
        l.Cells.Add(new LayoutCell(bigRow, bigCol, span, span));
        l.Normalize();
        return l;
    }

    /// <summary>
    /// Removes invalid / overlapping tiles and fills every uncovered base cell with a 1x1 tile.
    /// </summary>
    public void Normalize()
    {
        Rows = Math.Clamp(Rows, 1, 8);
        Columns = Math.Clamp(Columns, 1, 8);
        var valid = new List<LayoutCell>();
        foreach (var c in Cells)
        {
            if (c.Row < 0 || c.Column < 0 || c.RowSpan < 1 || c.ColumnSpan < 1) continue;
            if (c.Row + c.RowSpan > Rows || c.Column + c.ColumnSpan > Columns) continue;
            bool overlaps = valid.Any(v =>
                c.Row < v.Row + v.RowSpan && v.Row < c.Row + c.RowSpan &&
                c.Column < v.Column + v.ColumnSpan && v.Column < c.Column + c.ColumnSpan);
            if (!overlaps) valid.Add(c);
        }
        for (int r = 0; r < Rows; r++)
            for (int col = 0; col < Columns; col++)
                if (!valid.Any(v => v.Covers(r, col)))
                    valid.Add(new LayoutCell(r, col));
        Cells = valid;
    }

    public LayoutDefinition Clone() => new()
    {
        Id = Id, Name = Name, Rows = Rows, Columns = Columns, IsBuiltIn = IsBuiltIn,
        Cells = Cells.Select(c => c.Clone()).ToList(),
    };
}

/// <summary>Current multiview state: which layout and which camera in which slot.</summary>
public sealed class ViewState
{
    public string LayoutId { get; set; } = "4";

    /// <summary>Copy of the layout (used if the layout id no longer exists, e.g. a loaded saved view).</summary>
    public LayoutDefinition? Layout { get; set; }

    /// <summary>
    /// Camera per slot (null = empty tile). The list may be longer than the current layout,
    /// so switching to a smaller layout and back keeps the assignment.
    /// </summary>
    public List<Guid?> Slots { get; set; } = new();
}

/// <summary>A user saved view (layout + camera assignment).</summary>
public sealed class SavedView
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Ansicht";
    public LayoutDefinition Layout { get; set; } = new();
    public List<Guid?> Slots { get; set; } = new();
}
