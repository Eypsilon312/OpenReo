using OpenReo.Core.Models;

namespace OpenReo.Core.Layouts;

/// <summary>
/// Provides the built-in layouts and manages user defined layouts and saved views.
/// </summary>
public sealed class LayoutManager
{
    private readonly AppConfiguration _config;

    /// <summary>Layouts shown as direct buttons in the toolbar.</summary>
    public static readonly string[] PrimaryLayoutIds = { "1", "2", "4", "9", "16", "25" };

    public IReadOnlyList<LayoutDefinition> BuiltIn { get; }

    public LayoutManager(AppConfiguration config)
    {
        _config = config;
        var list = new List<LayoutDefinition>
        {
            LayoutDefinition.Grid("1", "1 Kamera", 1, 1),
            LayoutDefinition.Grid("2", "2 Kameras (1×2)", 1, 2),
            LayoutDefinition.Grid("4", "4 Kameras (2×2)", 2, 2),
            LayoutDefinition.Grid("6g", "6 Kameras (2×3)", 2, 3),
            LayoutDefinition.WithLargeTile("1+5", "1 groß + 5", 3, 3, 0, 0, 2),
            LayoutDefinition.WithLargeTile("1+7", "1 groß + 7", 4, 4, 0, 0, 3),
            LayoutDefinition.Grid("9", "9 Kameras (3×3)", 3, 3),
            LayoutDefinition.Grid("12", "12 Kameras (3×4)", 3, 4),
            LayoutDefinition.WithLargeTile("1+12", "1 groß (Mitte) + 12", 4, 4, 1, 1, 2),
            LayoutDefinition.Grid("16", "16 Kameras (4×4)", 4, 4),
            LayoutDefinition.WithLargeTile("1+16", "1 groß + 16", 5, 5, 0, 0, 3),
            LayoutDefinition.Grid("25", "25 Kameras (5×5)", 5, 5),
        };
        foreach (var l in list) l.IsBuiltIn = true;
        BuiltIn = list;
    }

    public IReadOnlyList<LayoutDefinition> Custom => _config.CustomLayouts;
    public IReadOnlyList<SavedView> SavedViews => _config.SavedViews;

    public IEnumerable<LayoutDefinition> All => BuiltIn.Concat(_config.CustomLayouts);

    public LayoutDefinition? Find(string? id) => All.FirstOrDefault(l => l.Id == id);

    public LayoutDefinition Default => BuiltIn.First(l => l.Id == "4");

    public void SaveCustomLayout(LayoutDefinition layout)
    {
        layout.Normalize();
        layout.IsBuiltIn = false;
        var idx = _config.CustomLayouts.FindIndex(l => l.Id == layout.Id);
        if (idx >= 0) _config.CustomLayouts[idx] = layout;
        else _config.CustomLayouts.Add(layout);
    }

    public void DeleteCustomLayout(string id) => _config.CustomLayouts.RemoveAll(l => l.Id == id);

    public SavedView SaveView(string name, LayoutDefinition layout, IEnumerable<Guid?> slots)
    {
        var existing = _config.SavedViews.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.CurrentCultureIgnoreCase));
        var view = existing ?? new SavedView();
        view.Name = name;
        view.Layout = layout.Clone();
        view.Slots = slots.Take(Math.Max(layout.TileCount, 1)).ToList();
        if (existing == null) _config.SavedViews.Add(view);
        return view;
    }

    public void DeleteView(Guid id) => _config.SavedViews.RemoveAll(v => v.Id == id);
}
