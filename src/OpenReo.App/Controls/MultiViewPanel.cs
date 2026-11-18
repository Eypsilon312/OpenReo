using System.Windows;
using System.Windows.Controls;
using OpenReo.App.ViewModels;
using OpenReo.Core.Models;
using OpenReo.Video;

namespace OpenReo.App.Controls;

/// <summary>Actions the multiview needs from the main window.</summary>
public interface IViewerActions
{
    void SetCameraQuality(CameraViewModel camera, StreamQuality quality);
    void EditCamera(CameraViewModel camera);
    void OpenCameraSettings(CameraViewModel camera);
    Guid? AudioCameraId { get; }
    void ToggleAudio(CameraViewModel camera);
}

/// <summary>
/// Multiview manager: arranges <see cref="VideoTile"/>s according to a
/// <see cref="LayoutDefinition"/>, keeps the slot → camera assignment and handles
/// maximize / restore. Tiles are pooled per slot index so that switching layouts keeps
/// existing tiles (and their streams) alive.
/// </summary>
public sealed class MultiViewPanel : Grid, ITileHost
{
    private readonly Grid _grid = new();
    private readonly List<VideoTile> _pool = new();
    private VideoTile? _maxTile;
    private int? _maximizedSlot;
    private VideoTile? _selected;

    private StreamManager _streams = null!;
    private AppSettings _settings = null!;
    private IViewerActions _actions = null!;
    private Func<IReadOnlyList<CameraViewModel>> _cameras = null!;

    /// <summary>Raised after the layout or the camera assignment changed (for persistence).</summary>
    public event Action? ViewChanged;

    public LayoutDefinition Layout { get; private set; } = LayoutDefinition.Grid("4", "4", 2, 2);

    /// <summary>Camera per slot. Longer than the layout when a bigger layout was used before.</summary>
    public List<Guid?> Slots { get; private set; } = new();

    public MultiViewPanel()
    {
        Background = (System.Windows.Media.Brush)Application.Current.Resources["BgBrush"];
        Children.Add(_grid);
    }

    public void Initialize(StreamManager streams, AppSettings settings, IViewerActions actions, Func<IReadOnlyList<CameraViewModel>> cameras)
    {
        _streams = streams;
        _settings = settings;
        _actions = actions;
        _cameras = cameras;
    }

    // ITileHost --------------------------------------------------------------
    public StreamManager Streams => _streams;
    public AppSettings Settings => _settings;
    public IReadOnlyList<CameraViewModel> AllCameras => _cameras();
    public bool IsMaximized => _maximizedSlot != null;

    private CameraViewModel? Find(Guid? id) => id == null ? null : _cameras().FirstOrDefault(c => c.Id == id);

    public void TileSelected(VideoTile tile)
    {
        if (_selected == tile) return;
        if (_selected != null) _selected.IsSelected = false;
        _selected = tile;
        tile.IsSelected = true;
    }

    public void TileDoubleClicked(VideoTile tile)
    {
        if (IsMaximized) Restore();
        else if (tile.Camera != null && Layout.TileCount > 1) Maximize(tile.SlotIndex);
    }

    public void TileDrop(VideoTile target, Guid? cameraId, int? sourceSlot)
    {
        EnsureSlots(Math.Max(target.SlotIndex, sourceSlot ?? 0) + 1);
        if (cameraId != null)
        {
            Slots[target.SlotIndex] = cameraId;
        }
        else if (sourceSlot is int src && src != target.SlotIndex)
        {
            // Swap the two tiles.
            (Slots[src], Slots[target.SlotIndex]) = (Slots[target.SlotIndex], Slots[src]);
        }
        BindTiles();
        TileSelected(target);
        ViewChanged?.Invoke();
    }

    public void AssignCamera(VideoTile tile, Guid? cameraId)
    {
        EnsureSlots(tile.SlotIndex + 1);
        Slots[tile.SlotIndex] = cameraId;
        BindTiles();
        ViewChanged?.Invoke();
    }

    public void SetCameraQuality(CameraViewModel camera, StreamQuality quality) => _actions.SetCameraQuality(camera, quality);
    public void EditCamera(CameraViewModel camera) => _actions.EditCamera(camera);
    public void OpenCameraSettings(CameraViewModel camera) => _actions.OpenCameraSettings(camera);
    public Guid? AudioCameraId => _actions.AudioCameraId;
    public void ToggleAudio(CameraViewModel camera) => _actions.ToggleAudio(camera);

    // Layout ------------------------------------------------------------------

    /// <summary>Applies a layout and slot assignment (e.g. on start or when a saved view is loaded).</summary>
    public void SetView(LayoutDefinition layout, List<Guid?> slots)
    {
        Slots = slots;
        SetLayout(layout, raiseChanged: false);
    }

    public void SetLayout(LayoutDefinition layout, bool raiseChanged = true)
    {
        if (IsMaximized) Restore(raiseChanged: false);
        Layout = layout;
        EnsureSlots(layout.TileCount);

        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions.Clear();
        for (int r = 0; r < layout.Rows; r++) _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int c = 0; c < layout.Columns; c++) _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < layout.Cells.Count; i++)
        {
            var cell = layout.Cells[i];
            var tile = GetTile(i);
            SetRow(tile, cell.Row);
            SetColumn(tile, cell.Column);
            SetRowSpan(tile, cell.RowSpan);
            SetColumnSpan(tile, cell.ColumnSpan);
            _grid.Children.Add(tile);
            tile.SetActive(true);
        }
        BindTiles();
        if (raiseChanged) ViewChanged?.Invoke();
    }

    private VideoTile GetTile(int index)
    {
        while (_pool.Count <= index)
            _pool.Add(new VideoTile(this) { SlotIndex = _pool.Count });
        return _pool[index];
    }

    private void EnsureSlots(int count)
    {
        while (Slots.Count < count) Slots.Add(null);
    }

    /// <summary>Binds every visible tile to its slot's camera; tiles outside the layout release their stream.</summary>
    public void BindTiles()
    {
        int count = Layout.TileCount;
        for (int i = 0; i < _pool.Count; i++)
        {
            var tile = _pool[i];
            if (i < count)
            {
                var cam = Find(Slots[i]);
                if (Slots[i] != null && cam == null) Slots[i] = null; // camera was deleted
                tile.Bind(cam);
            }
            else
            {
                tile.Unbind(); // stream stays connected for the release delay -> no reconnect when switching back
            }
        }
        if (_maxTile != null && _maximizedSlot is int ms)
            _maxTile.Bind(Find(Slots.ElementAtOrDefault(ms)));
        UpdateInViewFlags();
    }

    private void UpdateInViewFlags()
    {
        var visible = new HashSet<Guid>(Slots.Take(Layout.TileCount).Where(s => s != null).Select(s => s!.Value));
        foreach (var c in _cameras()) c.IsInView = visible.Contains(c.Id);
    }

    // Maximize ------------------------------------------------------------------

    public void Maximize(int slot)
    {
        var cam = Find(Slots.ElementAtOrDefault(slot));
        if (cam == null) return;
        _maximizedSlot = slot;
        _maxTile ??= new VideoTile(this);
        _maxTile.SlotIndex = slot;
        _maxTile.ResetZoom();
        if (!Children.Contains(_maxTile)) Children.Add(_maxTile);
        _maxTile.Visibility = Visibility.Visible;
        _maxTile.SetActive(true);
        _maxTile.Bind(cam); // same camera + quality -> the existing stream is shared, no reconnect
        _grid.Visibility = Visibility.Hidden;
        foreach (var t in _pool) t.SetActive(false);
        TileSelected(_maxTile);
    }

    public void Restore(bool raiseChanged = true)
    {
        if (_maximizedSlot == null) return;
        _maximizedSlot = null;
        if (_maxTile != null)
        {
            _maxTile.Unbind();
            _maxTile.Visibility = Visibility.Collapsed;
            Children.Remove(_maxTile);
        }
        _grid.Visibility = Visibility.Visible;
        for (int i = 0; i < Math.Min(_pool.Count, Layout.TileCount); i++) _pool[i].SetActive(true);
        if (_selected == _maxTile) _selected = null;
    }

    // Camera helpers ------------------------------------------------------------

    /// <summary>
    /// Shows a camera (sidebar double click): selected empty tile → first empty tile →
    /// selected tile. Returns false if the camera is already visible.
    /// </summary>
    public bool ShowCamera(Guid cameraId)
    {
        if (IsMaximized) Restore();
        int count = Layout.TileCount;
        EnsureSlots(count);
        int existing = Slots.Take(count).ToList().IndexOf(cameraId);
        if (existing >= 0) { TileSelected(_pool[existing]); return false; }

        int target;
        if (_selected != null && _selected != _maxTile && _selected.SlotIndex < count && Slots[_selected.SlotIndex] == null)
            target = _selected.SlotIndex;
        else
        {
            target = Slots.Take(count).ToList().IndexOf(null);
            if (target < 0) target = _selected != null && _selected != _maxTile && _selected.SlotIndex < count ? _selected.SlotIndex : count - 1;
        }
        Slots[target] = cameraId;
        BindTiles();
        TileSelected(_pool[target]);
        ViewChanged?.Invoke();
        return true;
    }

    /// <summary>Removes the camera from the visible slot it was dragged from (drop on the sidebar).</summary>
    public void ClearSlot(int slot)
    {
        if (slot < 0 || slot >= Slots.Count) return;
        Slots[slot] = null;
        BindTiles();
        ViewChanged?.Invoke();
    }

    /// <summary>Puts all cameras into the slots in list order (used on first start).</summary>
    public void FillWithCameras(IEnumerable<Guid> cameraIds)
    {
        int i = 0;
        EnsureSlots(Layout.TileCount);
        foreach (var id in cameraIds)
        {
            if (Slots.Contains(id)) continue;
            while (i < Slots.Count && Slots[i] != null) i++;
            if (i >= Layout.TileCount) break;
            Slots[i] = id;
        }
        BindTiles();
        ViewChanged?.Invoke();
    }

    /// <summary>Re-binds tiles of a camera (e.g. after its quality changed).</summary>
    public void RefreshCamera(Guid cameraId)
    {
        foreach (var t in _pool.Where(t => t.Camera?.Id == cameraId).ToList())
            t.Bind(t.Camera);
        if (_maxTile?.Camera?.Id == cameraId) _maxTile.Bind(_maxTile.Camera);
    }

    public void RemoveCamera(Guid cameraId)
    {
        if (_maxTile?.Camera?.Id == cameraId) Restore();
        for (int i = 0; i < Slots.Count; i++)
            if (Slots[i] == cameraId) Slots[i] = null;
        BindTiles();
        ViewChanged?.Invoke();
    }

    public void RefreshOverlays()
    {
        for (int i = 0; i < Math.Min(_pool.Count, Layout.TileCount); i++) _pool[i].RefreshOverlay();
        _maxTile?.RefreshOverlay();
    }

    public void ResetZoomAll()
    {
        foreach (var t in _pool) t.ResetZoom();
        _maxTile?.ResetZoom();
    }

    public void ReleaseAll()
    {
        foreach (var t in _pool) t.Unbind();
        _maxTile?.Unbind();
    }
}
