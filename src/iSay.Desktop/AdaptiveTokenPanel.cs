using System.Windows;
using System.Windows.Controls;

namespace iSay.Desktop;

/// <summary>词条等宽排列，最多四列；窄窗口减少列数，滚动仍由整页负责。</summary>
public sealed class AdaptiveTokenPanel : Panel
{
    private const double MinimumCellWidth = 174;
    private const double Gap = 10;
    private const double CellHeight = 46;
    public int Columns { get; private set; } = 1;

    private static int ColumnsFor(double width) => Math.Clamp((int)Math.Floor((width + Gap) / (MinimumCellWidth + Gap)), 1, 4);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? MinimumCellWidth * 4 + Gap * 3 : Math.Max(0, availableSize.Width);
        Columns = ColumnsFor(width);
        var cellWidth = Math.Max(0, (width - Gap * (Columns - 1)) / Columns);
        foreach (UIElement child in InternalChildren) child.Measure(new Size(cellWidth, CellHeight));
        var rows = (InternalChildren.Count + Columns - 1) / Columns;
        return new Size(width, rows == 0 ? 0 : rows * CellHeight + (rows - 1) * Gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Columns = ColumnsFor(finalSize.Width);
        var cellWidth = Math.Max(0, (finalSize.Width - Gap * (Columns - 1)) / Columns);
        for (var index = 0; index < InternalChildren.Count; index++)
            InternalChildren[index].Arrange(new Rect(index % Columns * (cellWidth + Gap), index / Columns * (CellHeight + Gap), cellWidth, CellHeight));
        return finalSize;
    }
}
