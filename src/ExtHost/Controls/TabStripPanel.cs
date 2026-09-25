using System.Windows;
using System.Windows.Controls;

namespace ExtHost.Controls;

/// <summary>分頁列面板：分頁寬度平均分配，最寬 MaxTabWidth，最窄 MinTabWidth。</summary>
public sealed class TabStripPanel : Panel
{
    public double MaxTabWidth { get; set; } = 240;
    public double MinTabWidth { get; set; } = 72;

    private double ItemWidth(Size available)
    {
        var count = InternalChildren.Count;
        if (count == 0)
        {
            return 0;
        }
        var w = double.IsInfinity(available.Width) ? MaxTabWidth : available.Width / count;
        return Math.Max(MinTabWidth, Math.Min(MaxTabWidth, w));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = ItemWidth(availableSize);
        var h = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(w, availableSize.Height));
            h = Math.Max(h, child.DesiredSize.Height);
        }
        var total = w * InternalChildren.Count;
        if (!double.IsInfinity(availableSize.Width))
        {
            total = Math.Min(total, availableSize.Width);
        }
        return new Size(total, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var w = ItemWidth(finalSize);
        var x = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(x, 0, w, finalSize.Height));
            x += w;
        }
        return finalSize;
    }
}
