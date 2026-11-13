using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Differ.App.Controls;

/// <summary>Draws one continuous fog field over the comparison window's WPF surface.</summary>
internal sealed class ThemeMistOverlay : FrameworkElement
{
    private const double DurationMilliseconds = 680;

    private readonly MatrixTransform _mistTransform = new();
    private readonly DrawingVisual _mist = new() { Opacity = 0 };
    private readonly VisualCollection _children;

    private Point _origin;
    private double _maxRadius;
    private double _initialElapsed;
    private long _startedAt;
    private bool _dark;
    private bool _running;

    public ThemeMistOverlay()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        _mist.Transform = _mistTransform;
        _children = new VisualCollection(this) { _mist };
        Unloaded += (_, _) => Stop();
        SizeChanged += (_, _) => Stop();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) Stop();
        };
    }

    protected override int VisualChildrenCount => _children?.Count ?? 0;

    protected override Visual GetVisualChild(int index) => _children[index];

    public event EventHandler? Stopped;

    public bool Start(Point origin, Size area, bool dark, double elapsedMilliseconds, bool automaticClock = true)
    {
        Stop();
        if (!IsVisible || area.IsEmpty || area.Width <= 0 || area.Height <= 0
            || !double.IsFinite(area.Width) || !double.IsFinite(area.Height)
            || !double.IsFinite(origin.X) || !double.IsFinite(origin.Y)
            || !double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds >= DurationMilliseconds
            || TryFindResource(dark ? "ThemeMist.Night" : "ThemeMist.Day") is not Brush mistBrush)
        {
            return false;
        }

        _origin = origin;
        _maxRadius = 1.35 * double.Hypot(area.Width, area.Height);
        _dark = dark;
        _initialElapsed = elapsedMilliseconds;
        _startedAt = Stopwatch.GetTimestamp();

        DrawUnitGradient(_mist, mistBrush);
        _running = true;
        if (automaticClock) CompositionTarget.Rendering += OnRendering;
        RenderFrame(_initialElapsed);
        return _running;
    }

    public void Stop()
    {
        if (!_running) return;

        _running = false;
        CompositionTarget.Rendering -= OnRendering;
        _mist.Opacity = 0;
        Stopped?.Invoke(this, EventArgs.Empty);
    }

    private void OnRendering(object? sender, EventArgs e) =>
        RenderFrame(_initialElapsed + Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds);

    internal void RenderFrame(double elapsedMilliseconds)
    {
        if (!_running) return;
        if (elapsedMilliseconds >= DurationMilliseconds)
        {
            Stop();
            return;
        }

        var progress = Math.Max(0, elapsedMilliseconds) / DurationMilliseconds;
        var eased = progress < 0.5
            ? 4 * progress * progress * progress
            : 1 - Math.Pow(2 - 2 * progress, 3) / 2;
        var radius = _maxRadius * (_dark ? 1 - eased : eased);
        var mistRadius = Math.Max(20, radius);
        _mistTransform.Matrix = new Matrix(mistRadius, 0, 0, mistRadius, _origin.X, _origin.Y);

        var opacity = Math.Sin(Math.Pow(progress, 0.88) * Math.PI);
        _mist.Opacity = opacity;
    }

    private static void DrawUnitGradient(DrawingVisual layer, Brush brush)
    {
        using var drawing = layer.RenderOpen();
        drawing.DrawEllipse(brush, null, new Point(0, 0), 1, 1);
    }
}
