using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using NitroPower.Core.Policy;

namespace NitroPower.Gui.Controls;

/// <summary>
/// Temperature → fan duty curve. Drag a point to move it, double-click an
/// empty spot to add one, right-click a point to remove it. The curve stays
/// valid while editing: temperatures rise left to right and the duty never
/// falls as the temperature rises.
/// </summary>
public sealed class FanCurveEditor : Control
{
    public const double MinTemp = 20, MaxTemp = 100;
    public const int MaxPoints = 10;

    public static readonly StyledProperty<IReadOnlyList<FanCurvePoint>?> PointsProperty =
        AvaloniaProperty.Register<FanCurveEditor, IReadOnlyList<FanCurvePoint>?>(nameof(Points),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<Color> CurveColorProperty =
        AvaloniaProperty.Register<FanCurveEditor, Color>(nameof(CurveColor), Colors.Teal);

    /// <summary>Current temperature, drawn as a marker (NaN: none).</summary>
    public static readonly StyledProperty<double> MarkerTemperatureProperty =
        AvaloniaProperty.Register<FanCurveEditor, double>(nameof(MarkerTemperature), double.NaN);

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<FanCurveEditor, IBrush?>(nameof(GridBrush), Brushes.DimGray);

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<FanCurveEditor, IBrush?>(nameof(LabelBrush), Brushes.Gray);

    public static readonly StyledProperty<IBrush?> SurfaceBrushProperty =
        AvaloniaProperty.Register<FanCurveEditor, IBrush?>(nameof(SurfaceBrush), Brushes.Black);

    static FanCurveEditor()
    {
        AffectsRender<FanCurveEditor>(PointsProperty, CurveColorProperty, MarkerTemperatureProperty,
            GridBrushProperty, LabelBrushProperty, SurfaceBrushProperty);
        FocusableProperty.OverrideDefaultValue<FanCurveEditor>(true);
    }

    public IReadOnlyList<FanCurvePoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public Color CurveColor { get => GetValue(CurveColorProperty); set => SetValue(CurveColorProperty, value); }
    public double MarkerTemperature { get => GetValue(MarkerTemperatureProperty); set => SetValue(MarkerTemperatureProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public IBrush? SurfaceBrush { get => GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }

    private int _drag = -1, _hover = -1;

    private const double Left = 44, Right = 14, Top = 14, Bottom = 30, HitRadius = 13;

    private Rect Plot => new(Left, Top, Math.Max(1, Bounds.Width - Left - Right), Math.Max(1, Bounds.Height - Top - Bottom));

    private Point ToScreen(FanCurvePoint p)
    {
        var r = Plot;
        return new Point(r.Left + r.Width * (p.TempC - MinTemp) / (MaxTemp - MinTemp), r.Bottom - r.Height * p.Percent / 100.0);
    }

    private (double Temp, double Percent) FromScreen(Point s)
    {
        var r = Plot;
        return (MinTemp + (s.X - r.Left) / r.Width * (MaxTemp - MinTemp), (r.Bottom - s.Y) / r.Height * 100);
    }

    private int HitTest(Point s)
    {
        var points = Points ?? [];
        var best = -1;
        var bestDist = HitRadius;
        for (var i = 0; i < points.Count; i++)
        {
            var p = ToScreen(points[i]);
            var d = Math.Sqrt(Math.Pow(p.X - s.X, 2) + Math.Pow(p.Y - s.Y, 2));
            if (d <= bestDist)
            {
                best = i;
                bestDist = d;
            }
        }
        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var points = Points?.ToList() ?? [];
        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        var hit = HitTest(pos);

        if (props.IsRightButtonPressed)
        {
            if (hit >= 0 && points.Count > 2)
            {
                points.RemoveAt(hit);
                Commit(points);
                _hover = -1;
            }
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed)
            return;
        if (hit >= 0)
        {
            _drag = hit;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2 && points.Count < MaxPoints)
        {
            var (t, p) = FromScreen(pos);
            var temp = Math.Round(Math.Clamp(t, MinTemp, MaxTemp));
            if (points.Any(x => Math.Abs(x.TempC - temp) < 1))
                return;
            var index = points.FindIndex(x => x.TempC > temp);
            if (index < 0)
                index = points.Count;
            var lo = index > 0 ? points[index - 1].Percent : FanCurve.MinPercent;
            var hi = index < points.Count ? points[index].Percent : FanCurve.MaxPercent;
            points.Insert(index, new FanCurvePoint(temp, (int)Math.Clamp(Math.Round(p), lo, hi)));
            Commit(points);
            _drag = index;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (_drag < 0)
        {
            var hover = HitTest(pos);
            if (hover != _hover)
            {
                _hover = hover;
                Cursor = hover >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
                InvalidateVisual();
            }
            return;
        }
        var points = Points?.ToList() ?? [];
        if (_drag >= points.Count)
            return;
        var (t, p) = FromScreen(pos);
        var minT = _drag > 0 ? points[_drag - 1].TempC + 1 : MinTemp;
        var maxT = _drag < points.Count - 1 ? points[_drag + 1].TempC - 1 : MaxTemp;
        var minP = _drag > 0 ? points[_drag - 1].Percent : FanCurve.MinPercent;
        var maxP = _drag < points.Count - 1 ? points[_drag + 1].Percent : FanCurve.MaxPercent;
        var moved = new FanCurvePoint(
            Math.Round(Math.Clamp(t, minT, Math.Max(minT, maxT))),
            (int)Math.Round(Math.Clamp(p, minP, Math.Max(minP, maxP))));
        if (moved == points[_drag])
            return;
        points[_drag] = moved;
        _hover = _drag;
        Commit(points);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag >= 0)
        {
            _drag = -1;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_drag < 0 && _hover >= 0)
        {
            _hover = -1;
            InvalidateVisual();
        }
    }

    private void Commit(List<FanCurvePoint> points) => SetCurrentValue(PointsProperty, points.AsReadOnly());

    /// <summary>Linear interpolation, flat outside the points (as the daemon does).</summary>
    public static double Evaluate(IReadOnlyList<FanCurvePoint> points, double temp)
    {
        if (points.Count == 0)
            return 0;
        if (temp <= points[0].TempC)
            return points[0].Percent;
        for (var i = 1; i < points.Count; i++)
        {
            if (temp <= points[i].TempC)
            {
                var (a, b) = (points[i - 1], points[i]);
                return a.Percent + (temp - a.TempC) / (b.TempC - a.TempC) * (b.Percent - a.Percent);
            }
        }
        return points[^1].Percent;
    }

    public override void Render(DrawingContext context)
    {
        var r = Plot;
        if (r.Width < 40 || r.Height < 40)
            return;
        var typeface = new Typeface(FontFamily.Default);
        var culture = CultureInfo.CurrentCulture;
        FormattedText Text(string s, double size = 10.5, IBrush? brush = null) =>
            new(s, culture, FlowDirection.LeftToRight, typeface, size, brush ?? LabelBrush);

        context.DrawRectangle(SurfaceBrush, null, new RoundedRect(r, 8));
        var grid = new Pen(GridBrush, 1, dashStyle: new DashStyle([3, 4], 0));
        for (var pct = 0; pct <= 100; pct += 20)
        {
            var y = r.Bottom - r.Height * pct / 100;
            if (pct is > 0 and < 100)
                context.DrawLine(grid, new Point(r.Left, y), new Point(r.Right, y));
            var label = Text($"{pct}%");
            context.DrawText(label, new Point(Left - 8 - label.Width, y - label.Height / 2));
        }
        for (var t = MinTemp; t <= MaxTemp; t += 10)
        {
            var x = r.Left + r.Width * (t - MinTemp) / (MaxTemp - MinTemp);
            if (t > MinTemp && t < MaxTemp)
                context.DrawLine(grid, new Point(x, r.Top), new Point(x, r.Bottom));
            var label = Text($"{t:0}°");
            context.DrawText(label, new Point(x - label.Width / 2, r.Bottom + 8));
        }

        var points = Points ?? [];
        if (points.Count == 0)
            return;
        var color = CurveColor;
        var screen = points.Select(ToScreen).ToList();
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(new Point(r.Left, screen[0].Y), false);
            foreach (var p in screen)
                g.LineTo(p);
            g.LineTo(new Point(r.Right, screen[^1].Y));
            g.EndFigure(false);
        }
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(r.Left, r.Bottom), true);
            g.LineTo(new Point(r.Left, screen[0].Y));
            foreach (var p in screen)
                g.LineTo(p);
            g.LineTo(new Point(r.Right, screen[^1].Y));
            g.LineTo(new Point(r.Right, r.Bottom));
            g.EndFigure(true);
        }
        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(80, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(8, color.R, color.G, color.B), 1),
            },
        };
        using (context.PushClip(new RoundedRect(r, 8)))
            context.DrawGeometry(fill, null, area);
        var stroke = new SolidColorBrush(color);
        context.DrawGeometry(null, new Pen(stroke, 2.5, lineJoin: PenLineJoin.Round), line);

        // Marker: current temperature and the duty the curve gives
        if (!double.IsNaN(MarkerTemperature))
        {
            var t = Math.Clamp(MarkerTemperature, MinTemp, MaxTemp);
            var duty = Evaluate(points, MarkerTemperature);
            var x = r.Left + r.Width * (t - MinTemp) / (MaxTemp - MinTemp);
            var y = r.Bottom - r.Height * duty / 100;
            context.DrawLine(new Pen(LabelBrush, 1.2, dashStyle: new DashStyle([2, 3], 0)), new Point(x, r.Top), new Point(x, r.Bottom));
            context.DrawEllipse(Brushes.White, new Pen(stroke, 2), new Point(x, y), 4.5, 4.5);
            var tag = Text($"{MarkerTemperature:0} °C → {duty:0}%", 11, stroke);
            var tx = Math.Clamp(x + 8, r.Left + 4, r.Right - tag.Width - 4);
            context.DrawText(tag, new Point(tx, r.Top + 6));
        }

        for (var i = 0; i < screen.Count; i++)
        {
            var active = i == _hover || i == _drag;
            context.DrawEllipse(SurfaceBrush, new Pen(stroke, 2.5), screen[i], active ? 7.5 : 6, active ? 7.5 : 6);
            if (active)
                context.DrawEllipse(stroke, null, screen[i], 3.5, 3.5);
            if (!active)
                continue;
            var tag = Text($"{points[i].TempC:0} °C · {points[i].Percent}%", 11.5, Brushes.White);
            var box = new Rect(screen[i].X - tag.Width / 2 - 8, screen[i].Y - tag.Height - 22, tag.Width + 16, tag.Height + 8);
            box = new Rect(Math.Clamp(box.X, r.Left, r.Right - box.Width), Math.Max(r.Top, box.Y), box.Width, box.Height);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(235, 17, 24, 39)), null, new RoundedRect(box, 6));
            context.DrawText(tag, new Point(box.X + 8, box.Y + 4));
        }
    }
}
