using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NitroPower.Gui.Controls;

/// <summary>The last N samples of one sensor (oldest first). NaN = no reading.</summary>
public sealed class ChartSeries(Color color, int capacity = 120)
{
    private readonly double[] _values = new double[capacity];
    private int _start, _count;

    public Color Color { get; } = color;
    public int Capacity => _values.Length;
    public int Count => _count;

    public double this[int i] => _values[(_start + i) % _values.Length];

    public double? Last => _count == 0 || double.IsNaN(this[_count - 1]) ? null : this[_count - 1];

    public void Add(double? value)
    {
        var v = value ?? double.NaN;
        if (_count < _values.Length)
        {
            _values[(_start + _count) % _values.Length] = v;
            _count++;
        }
        else
        {
            _values[_start] = v;
            _start = (_start + 1) % _values.Length;
        }
    }
}

/// <summary>Line chart of recent history, newest at the right edge.</summary>
public sealed class HistoryChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty =
        AvaloniaProperty.Register<HistoryChart, IReadOnlyList<ChartSeries>?>(nameof(Series));

    /// <summary>Bump to redraw after adding samples.</summary>
    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<HistoryChart, int>(nameof(Revision));

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<HistoryChart, double>(nameof(Minimum));

    /// <summary>NaN: the largest value seen, rounded up.</summary>
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<HistoryChart, double>(nameof(Maximum), double.NaN);

    public static readonly StyledProperty<string> UnitProperty =
        AvaloniaProperty.Register<HistoryChart, string>(nameof(Unit), "");

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<HistoryChart, IBrush?>(nameof(GridBrush), Brushes.DimGray);

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<HistoryChart, IBrush?>(nameof(LabelBrush), Brushes.Gray);

    static HistoryChart() => AffectsRender<HistoryChart>(SeriesProperty, RevisionProperty, MinimumProperty,
        MaximumProperty, GridBrushProperty, LabelBrushProperty, UnitProperty);

    public IReadOnlyList<ChartSeries>? Series { get => GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public int Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    private const double LabelWidth = 40, Pad = 6;

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < LabelWidth + 20 || h < 30)
            return;
        var series = Series ?? [];
        var min = Minimum;
        var max = Maximum;
        if (double.IsNaN(max))
        {
            max = min + 1;
            foreach (var s in series)
            {
                for (var i = 0; i < s.Count; i++)
                {
                    if (!double.IsNaN(s[i]))
                        max = Math.Max(max, s[i]);
                }
            }
            max = NiceCeiling(max);
        }

        var plot = new Rect(LabelWidth, Pad, w - LabelWidth - Pad, h - 2 * Pad);
        var typeface = new Typeface(FontFamily.Default);
        var gridPen = new Pen(GridBrush, 1, dashStyle: new DashStyle([3, 4], 0));
        const int lines = 4;
        for (var i = 0; i <= lines; i++)
        {
            var y = plot.Bottom - plot.Height * i / lines;
            context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var value = min + (max - min) * i / lines;
            var text = new FormattedText(Format(value) + Unit, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, 10.5, LabelBrush);
            context.DrawText(text, new Point(LabelWidth - 8 - text.Width, y - text.Height / 2));
        }

        foreach (var s in series)
        {
            if (s.Count < 2)
                continue;
            var step = plot.Width / (s.Capacity - 1);
            var x0 = plot.Right - step * (s.Count - 1);
            Point P(int i) => new(x0 + step * i,
                plot.Bottom - plot.Height * Math.Clamp((s[i] - min) / (max - min), 0, 1));

            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var lc = line.Open())
            using (var ac = area.Open())
            {
                var open = false;
                var lastX = 0.0;
                for (var i = 0; i < s.Count; i++)
                {
                    if (double.IsNaN(s[i]))
                    {
                        if (open)
                        {
                            lc.EndFigure(false);
                            ac.LineTo(new Point(lastX, plot.Bottom));
                            ac.EndFigure(true);
                            open = false;
                        }
                        continue;
                    }
                    var p = P(i);
                    if (!open)
                    {
                        lc.BeginFigure(p, false);
                        ac.BeginFigure(new Point(p.X, plot.Bottom), true);
                        ac.LineTo(p);
                        open = true;
                    }
                    else
                    {
                        lc.LineTo(p);
                        ac.LineTo(p);
                    }
                    lastX = p.X;
                }
                if (open)
                {
                    lc.EndFigure(false);
                    ac.LineTo(new Point(lastX, plot.Bottom));
                    ac.EndFigure(true);
                }
            }
            var fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(70, s.Color.R, s.Color.G, s.Color.B), 0),
                    new GradientStop(Color.FromArgb(0, s.Color.R, s.Color.G, s.Color.B), 1),
                },
            };
            using (context.PushClip(plot.Inflate(2)))
            {
                context.DrawGeometry(fill, null, area);
                context.DrawGeometry(null, new Pen(new SolidColorBrush(s.Color), 2, lineJoin: PenLineJoin.Round), line);
                if (s.Last is not null)
                {
                    var last = P(s.Count - 1);
                    context.DrawEllipse(new SolidColorBrush(s.Color), null, last, 3.5, 3.5);
                }
            }
        }
    }

    private static string Format(double v) =>
        Math.Abs(v) >= 1000 ? (v / 1000).ToString("0.#k", CultureInfo.CurrentCulture) : v.ToString("0", CultureInfo.CurrentCulture);

    private static double NiceCeiling(double v)
    {
        if (v <= 0)
            return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(v)));
        // Multiples that split into four round steps
        foreach (var f in new[] { 1, 2, 4, 6, 8, 10 })
        {
            if (f * magnitude >= v)
                return f * magnitude;
        }
        return 10 * magnitude;
    }
}
