using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace OpenReo.App.Controls;

/// <summary>
/// Time line of one day with the recorded segments.
/// Mouse wheel: zoom (24 h … 5 min) around the cursor · drag: scroll · click: jump to that time.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    private static readonly TimeSpan MinSpan = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxSpan = TimeSpan.FromHours(24);

    private readonly Brush _bg = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x18));
    private readonly Brush _segment = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private readonly Brush _segmentDim = new SolidColorBrush(Color.FromArgb(0x60, 0xF5, 0x9E, 0x0B));
    private readonly Pen _tick = new(new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x50)), 1);
    private readonly Pen _playhead = new(new SolidColorBrush(Color.FromRgb(0xF2, 0x55, 0x55)), 2);
    private readonly Pen _hover = new(new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)), 1);
    private readonly Brush _text = new SolidColorBrush(Color.FromRgb(0x8D, 0x95, 0xA5));
    private readonly Brush _labelBg = new SolidColorBrush(Color.FromArgb(0xE0, 0x22, 0x26, 0x30));
    private readonly Typeface _font = new("Segoe UI");

    private IReadOnlyList<(DateTime Start, DateTime End, bool Exact)> _segments = Array.Empty<(DateTime, DateTime, bool)>();
    private DateTime _day = DateTime.Today;
    private DateTime _viewStart = DateTime.Today;
    private TimeSpan _viewSpan = MaxSpan;
    private DateTime? _position;
    private Point? _mouseDown;
    private DateTime _dragViewStart;
    private bool _dragging;
    private double? _hoverX;

    /// <summary>Raised when the user clicked a time.</summary>
    public event Action<DateTime>? SeekRequested;

    public TimelineControl()
    {
        Height = 64;
        Cursor = Cursors.Hand;
        ClipToBounds = true;
        Focusable = false;
    }

    public DateTime Day => _day;

    public void SetDay(DateTime day, IReadOnlyList<(DateTime Start, DateTime End, bool Exact)> segments)
    {
        _day = day.Date;
        _segments = segments;
        _viewSpan = MaxSpan;
        _viewStart = _day;
        InvalidateVisual();
    }

    public void SetSegments(IReadOnlyList<(DateTime Start, DateTime End, bool Exact)> segments)
    {
        _segments = segments;
        InvalidateVisual();
    }

    /// <summary>Current playback time (keeps the playhead visible while zoomed in).</summary>
    public DateTime? Position
    {
        get => _position;
        set
        {
            _position = value;
            if (value is DateTime p && _viewSpan < MaxSpan && !_dragging && (p < _viewStart || p > _viewStart + _viewSpan))
                ClampView(p - TimeSpan.FromTicks(_viewSpan.Ticks / 4));
            InvalidateVisual();
        }
    }

    private double X(DateTime t) => (t - _viewStart).TotalSeconds / _viewSpan.TotalSeconds * ActualWidth;
    private DateTime T(double x) => _viewStart + TimeSpan.FromSeconds(x / Math.Max(1, ActualWidth) * _viewSpan.TotalSeconds);

    private void ClampView(DateTime start)
    {
        var max = _day + MaxSpan - _viewSpan;
        _viewStart = start < _day ? _day : start > max ? max : start;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRoundedRectangle(_bg, null, new Rect(0, 0, w, h), 4, 4);
        double barTop = 22, barH = h - 30;

        // recorded segments (dim = duration still estimated)
        foreach (var (s, e, exact) in _segments)
        {
            if (e < _viewStart || s > _viewStart + _viewSpan) continue;
            double x1 = Math.Max(0, X(s)), x2 = Math.Min(w, X(e));
            if (x2 - x1 < 1) x2 = x1 + 1;
            dc.DrawRectangle(exact ? _segment : _segmentDim, null, new Rect(x1, barTop, x2 - x1, barH));
        }

        // ticks + labels
        var step = PickStep(_viewSpan);
        var first = new DateTime((_viewStart.Ticks / step.Ticks) * step.Ticks);
        for (var t = first; t <= _viewStart + _viewSpan; t += step)
        {
            double x = X(t);
            if (x < 0) continue;
            dc.DrawLine(_tick, new Point(x, barTop - 4), new Point(x, h - 4));
            var label = t.ToString(step < TimeSpan.FromMinutes(1) ? "HH:mm:ss" : "HH:mm", CultureInfo.CurrentCulture);
            var ft = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _font, 10.5, _text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(Math.Min(x + 3, w - ft.Width - 2), 3));
        }

        // hover
        if (_hoverX is double hx && !_dragging)
        {
            dc.DrawLine(_hover, new Point(hx, barTop - 4), new Point(hx, h - 4));
            DrawBadge(dc, T(hx).ToString("HH:mm:ss", CultureInfo.CurrentCulture), hx, h - 2, _hover.Brush);
        }

        // playhead
        if (_position is DateTime p && p >= _viewStart && p <= _viewStart + _viewSpan)
        {
            double x = X(p);
            dc.DrawLine(_playhead, new Point(x, barTop - 6), new Point(x, h - 2));
        }
    }

    private void DrawBadge(DrawingContext dc, string text, double x, double bottom, Brush fg)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _font, 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double bx = Math.Clamp(x - ft.Width / 2 - 4, 0, ActualWidth - ft.Width - 8);
        dc.DrawRoundedRectangle(_labelBg, null, new Rect(bx, bottom - ft.Height - 4, ft.Width + 8, ft.Height + 2), 3, 3);
        dc.DrawText(ft, new Point(bx + 4, bottom - ft.Height - 3));
    }

    private static TimeSpan PickStep(TimeSpan span) => span.TotalMinutes switch
    {
        > 12 * 60 => TimeSpan.FromHours(2),
        > 6 * 60 => TimeSpan.FromHours(1),
        > 3 * 60 => TimeSpan.FromMinutes(30),
        > 90 => TimeSpan.FromMinutes(15),
        > 40 => TimeSpan.FromMinutes(5),
        > 12 => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(1),
    };

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var anchor = T(e.GetPosition(this).X);
        double factor = e.Delta > 0 ? 0.5 : 2.0;
        var span = TimeSpan.FromTicks((long)(_viewSpan.Ticks * factor));
        if (span < MinSpan) span = MinSpan;
        if (span > MaxSpan) span = MaxSpan;
        double rel = (anchor - _viewStart).TotalSeconds / _viewSpan.TotalSeconds;
        _viewSpan = span;
        ClampView(anchor - TimeSpan.FromSeconds(rel * span.TotalSeconds));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _mouseDown = e.GetPosition(this);
        _dragViewStart = _viewStart;
        _dragging = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        _hoverX = p.X;
        if (_mouseDown is Point d && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_dragging && Math.Abs(p.X - d.X) > 4) _dragging = true;
            if (_dragging)
            {
                var delta = TimeSpan.FromSeconds((d.X - p.X) / Math.Max(1, ActualWidth) * _viewSpan.TotalSeconds);
                ClampView(_dragViewStart + delta);
                Cursor = Cursors.SizeWE;
            }
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        Cursor = Cursors.Hand;
        if (_mouseDown != null && !_dragging) SeekRequested?.Invoke(T(e.GetPosition(this).X));
        _mouseDown = null;
        _dragging = false;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverX = null;
        InvalidateVisual();
    }
}
