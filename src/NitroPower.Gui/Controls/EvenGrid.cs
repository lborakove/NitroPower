using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace NitroPower.Gui.Controls;

/// <summary>
/// Equal-size cells, filled row by row, with optional spacing between them.
/// It replaces UniformGrid, which in Avalonia 12 rounds the cell width and then
/// multiplies it by the column index: the error grows by up to half a pixel per
/// column, so the last column can end past the panel and lose its right border.
/// Here every cell edge is rounded on its own, so the grid always fits exactly.
/// </summary>
public sealed class EvenGrid : Panel
{
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<EvenGrid, int>(nameof(Columns), 1);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<EvenGrid, double>(nameof(ColumnSpacing));

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<EvenGrid, double>(nameof(RowSpacing));

    static EvenGrid() => AffectsMeasure<EvenGrid>(ColumnsProperty, ColumnSpacingProperty, RowSpacingProperty);

    public int Columns { get => GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double ColumnSpacing { get => GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }
    public double RowSpacing { get => GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }

    private int ColumnCount => Math.Max(1, Columns);

    private int RowCount(int visible) => Math.Max(1, (visible + ColumnCount - 1) / ColumnCount);

    private static double Cell(double total, int count, double spacing) =>
        double.IsInfinity(total) ? double.PositiveInfinity : Math.Max(0, (total - (count - 1) * spacing) / count);

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = ColumnCount;
        var rows = RowCount(Children.Count(c => c.IsVisible));
        var cell = new Size(Cell(availableSize.Width, columns, ColumnSpacing), Cell(availableSize.Height, rows, RowSpacing));

        double width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(cell);
            width = Math.Max(width, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(width * columns + (columns - 1) * ColumnSpacing, height * rows + (rows - 1) * RowSpacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Children.Where(c => c.IsVisible).ToList();
        var columns = ColumnCount;
        var rows = RowCount(visible.Count);
        var cellWidth = Cell(finalSize.Width, columns, ColumnSpacing);
        var cellHeight = Cell(finalSize.Height, rows, RowSpacing);
        var scale = LayoutHelper.GetLayoutScale(this);
        double Snap(double v) => UseLayoutRounding ? LayoutHelper.RoundLayoutValue(v, scale) : v;

        for (var i = 0; i < visible.Count; i++)
        {
            int column = i % columns, row = i / columns;
            var left = Snap(column * (cellWidth + ColumnSpacing));
            var right = Snap(column * (cellWidth + ColumnSpacing) + cellWidth);
            var top = Snap(row * (cellHeight + RowSpacing));
            var bottom = Snap(row * (cellHeight + RowSpacing) + cellHeight);
            visible[i].Arrange(new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top)));
        }
        return finalSize;
    }
}
