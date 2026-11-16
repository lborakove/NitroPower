using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NitroPower.Gui.Controls;

/// <summary>A schematic keyboard lit in four zones, left to right.</summary>
public sealed class KeyboardPreview : Control
{
    public static readonly StyledProperty<Color> Zone1Property = AvaloniaProperty.Register<KeyboardPreview, Color>(nameof(Zone1), Colors.White);
    public static readonly StyledProperty<Color> Zone2Property = AvaloniaProperty.Register<KeyboardPreview, Color>(nameof(Zone2), Colors.White);
    public static readonly StyledProperty<Color> Zone3Property = AvaloniaProperty.Register<KeyboardPreview, Color>(nameof(Zone3), Colors.White);
    public static readonly StyledProperty<Color> Zone4Property = AvaloniaProperty.Register<KeyboardPreview, Color>(nameof(Zone4), Colors.White);

    /// <summary>0..100.</summary>
    public static readonly StyledProperty<double> BrightnessProperty = AvaloniaProperty.Register<KeyboardPreview, double>(nameof(Brightness), 100);

    public static readonly StyledProperty<IBrush?> FrameBrushProperty = AvaloniaProperty.Register<KeyboardPreview, IBrush?>(nameof(FrameBrush), Brushes.Black);

    static KeyboardPreview() => AffectsRender<KeyboardPreview>(Zone1Property, Zone2Property, Zone3Property, Zone4Property,
        BrightnessProperty, FrameBrushProperty);

    public Color Zone1 { get => GetValue(Zone1Property); set => SetValue(Zone1Property, value); }
    public Color Zone2 { get => GetValue(Zone2Property); set => SetValue(Zone2Property, value); }
    public Color Zone3 { get => GetValue(Zone3Property); set => SetValue(Zone3Property, value); }
    public Color Zone4 { get => GetValue(Zone4Property); set => SetValue(Zone4Property, value); }
    public double Brightness { get => GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }
    public IBrush? FrameBrush { get => GetValue(FrameBrushProperty); set => SetValue(FrameBrushProperty, value); }

    // Key widths per row, in units; the last 4 units are the number pad (zone 4)
    private static readonly double[][] Rows =
    [
        [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1],
        [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 1, 1, 1, 1],
        [1.5, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1.5, 1, 1, 1, 1],
        [1.8, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2.2, 1, 1, 1, 1],
        [2.3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1.7, 1, 1, 1, 1, 1],
        [1.3, 1, 1, 1.3, 5.6, 1.3, 1, 1, 1, 1, 1, 1, 1, 1],
    ];

    public override void Render(DrawingContext context)
    {
        var b = Bounds;
        if (b.Width < 100 || b.Height < 40)
            return;
        context.DrawRectangle(FrameBrush, null, new RoundedRect(new Rect(0, 0, b.Width, b.Height), 12));
        const double pad = 12, gap = 4;
        var unitsPerRow = Rows.Max(r => r.Sum());
        var unit = (b.Width - 2 * pad - gap * 18) / unitsPerRow;
        var keyH = (b.Height - 2 * pad - gap * (Rows.Length - 1)) / Rows.Length;
        var level = Math.Clamp(Brightness / 100.0, 0, 1);
        var totalWidth = b.Width - 2 * pad;
        Color[] zones = [Zone1, Zone2, Zone3, Zone4];

        for (var row = 0; row < Rows.Length; row++)
        {
            var widths = Rows[row];
            var scale = (totalWidth - gap * (widths.Length - 1)) / (widths.Sum() * unit);
            var x = pad;
            var y = pad + row * (keyH + gap);
            foreach (var w in widths)
            {
                var width = w * unit * scale;
                var center = (x - pad + width / 2) / totalWidth;
                var zone = center switch { < 0.3 => 0, < 0.55 => 1, < 0.79 => 2, _ => 3 };
                var c = zones[zone];
                var alpha = (byte)(40 + 215 * level);
                var key = new Rect(x, y, width, keyH);
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)), null, new RoundedRect(key, 3));
                x += width + gap;
            }
        }
    }
}
