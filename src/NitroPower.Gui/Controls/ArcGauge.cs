using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NitroPower.Gui.Controls;

/// <summary>A 270° ring that fills with the value (temperatures, load).</summary>
public sealed class ArcGauge : Control
{
    public static readonly StyledProperty<double?> ValueProperty =
        AvaloniaProperty.Register<ArcGauge, double?>(nameof(Value));

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<ArcGauge, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<ArcGauge, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<ArcGauge, double>(nameof(Thickness), 10);

    public static readonly StyledProperty<IBrush?> ValueBrushProperty =
        AvaloniaProperty.Register<ArcGauge, IBrush?>(nameof(ValueBrush), Brushes.Teal);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ArcGauge, IBrush?>(nameof(TrackBrush), Brushes.Gray);

    /// <summary>Above this the ring turns <see cref="HotBrush"/>.</summary>
    public static readonly StyledProperty<double> HotThresholdProperty =
        AvaloniaProperty.Register<ArcGauge, double>(nameof(HotThreshold), double.NaN);

    public static readonly StyledProperty<IBrush?> HotBrushProperty =
        AvaloniaProperty.Register<ArcGauge, IBrush?>(nameof(HotBrush));

    static ArcGauge() => AffectsRender<ArcGauge>(ValueProperty, MinimumProperty, MaximumProperty, ThicknessProperty,
        ValueBrushProperty, TrackBrushProperty, HotThresholdProperty, HotBrushProperty);

    public double? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public IBrush? ValueBrush { get => GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public double HotThreshold { get => GetValue(HotThresholdProperty); set => SetValue(HotThresholdProperty, value); }
    public IBrush? HotBrush { get => GetValue(HotBrushProperty); set => SetValue(HotBrushProperty, value); }

    private const double StartDeg = 135, SweepDeg = 270;

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= Thickness * 2)
            return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = (size - Thickness) / 2;

        context.DrawGeometry(null, new Pen(TrackBrush, Thickness, lineCap: PenLineCap.Round), Arc(center, radius, SweepDeg));
        if (Value is not { } v || Maximum <= Minimum)
            return;
        var fraction = Math.Clamp((v - Minimum) / (Maximum - Minimum), 0, 1);
        if (fraction <= 0.002)
            return;
        var brush = !double.IsNaN(HotThreshold) && v >= HotThreshold && HotBrush is not null ? HotBrush : ValueBrush;
        context.DrawGeometry(null, new Pen(brush, Thickness, lineCap: PenLineCap.Round), Arc(center, radius, SweepDeg * fraction));
    }

    private static StreamGeometry Arc(Point c, double r, double sweep)
    {
        static Point At(Point c, double r, double deg)
        {
            var rad = deg * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(At(c, r, StartDeg), false);
        ctx.ArcTo(At(c, r, StartDeg + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise);
        ctx.EndFigure(false);
        return g;
    }
}
