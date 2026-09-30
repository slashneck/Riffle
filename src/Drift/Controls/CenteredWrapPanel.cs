using System.Windows;
using System.Windows.Controls;

namespace Drift.Controls;

// Like a WrapPanel, but every row is centered instead of hugging the left edge.
public sealed class CenteredWrapPanel : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        double rowWidth = 0, rowHeight = 0, width = 0, height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(available.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (rowWidth > 0 && rowWidth + size.Width > available.Width)
            {
                width = Math.Max(width, rowWidth);
                height += rowHeight;
                rowWidth = rowHeight = 0;
            }
            rowWidth += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Size(Math.Max(width, rowWidth), height + rowHeight);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var row = new List<UIElement>();
        double rowWidth = 0, rowHeight = 0, y = 0;

        void Flush()
        {
            double x = (final.Width - rowWidth) / 2;
            foreach (var item in row)
            {
                item.Arrange(new Rect(x, y, item.DesiredSize.Width, rowHeight));
                x += item.DesiredSize.Width;
            }
            y += rowHeight;
            row.Clear();
            rowWidth = rowHeight = 0;
        }

        foreach (UIElement child in InternalChildren)
        {
            var size = child.DesiredSize;
            if (row.Count > 0 && rowWidth + size.Width > final.Width)
                Flush();
            row.Add(child);
            rowWidth += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        if (row.Count > 0)
            Flush();

        return final;
    }
}
