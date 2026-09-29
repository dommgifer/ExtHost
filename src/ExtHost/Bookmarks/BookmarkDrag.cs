using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using ExtHost.Services;

namespace ExtHost.Bookmarks;

/// <summary>書籤拖放的資料格式：拖書籤、拖分頁、從網頁拖連結進來。</summary>
public static class BookmarkDrag
{
    /// <summary>ExtHost 內部拖曳書籤：值為以逗號分隔的書籤 id。</summary>
    private const string NodesFormat = "ExtHost.BookmarkIds";

    /// <summary>ExtHost 內部拖曳分頁：值為「網址\n標題」。</summary>
    private const string PageFormat = "ExtHost.Page";

    public static DataObject ForNodes(IReadOnlyList<BookmarkNode> nodes)
    {
        var data = new DataObject();
        data.SetData(NodesFormat, string.Join(",", nodes.Select(n => n.Id)));
        var urls = nodes.SelectMany(n => n.IsFolder ? n.Descendants() : new[] { n })
            .Where(n => !n.IsFolder)
            .Select(n => n.Url!)
            .ToList();
        if (urls.Count > 0)
        {
            // 拖到網頁、網址列或其他程式時當成網址文字
            data.SetText(string.Join(Environment.NewLine, urls));
        }
        return data;
    }

    public static DataObject ForPage(string url, string title)
    {
        var data = new DataObject();
        data.SetData(PageFormat, url + "\n" + title);
        data.SetText(url);
        return data;
    }

    /// <summary>取得拖曳中的書籤；不是書籤則回傳 null。</summary>
    public static List<BookmarkNode>? GetNodes(IDataObject data, BookmarkStore store)
    {
        if (!data.GetDataPresent(NodesFormat) || data.GetData(NodesFormat) is not string ids)
        {
            return null;
        }
        var list = new List<BookmarkNode>();
        foreach (var part in ids.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(part, out var id) && store.FindById(id) is { IsRoot: false } n)
            {
                list.Add(n);
            }
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>取得拖曳中的網頁（分頁、網頁連結、網址文字）。</summary>
    public static (string Url, string Title)? GetPage(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(PageFormat) && data.GetData(PageFormat) is string page)
            {
                var parts = page.Split('\n', 2);
                return (parts[0], parts.Length > 1 ? parts[1] : parts[0]);
            }

            string? url = null;
            if (data.GetDataPresent("UniformResourceLocatorW") && data.GetData("UniformResourceLocatorW") is MemoryStream ms)
            {
                url = Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0').Trim();
            }
            else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text)
            {
                var first = text.Trim().Split('\n')[0].Trim();
                // 只接受看起來像網址的文字
                if (!first.Contains(' ') && (first.Contains("://") || first.StartsWith("about:", StringComparison.OrdinalIgnoreCase)))
                {
                    url = first;
                }
            }
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }
            return (url, LinkTitle(data) ?? url);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>從網頁拖連結時，瀏覽器會附上「標題.url」捷徑檔名，從這裡取出連結文字。</summary>
    private static string? LinkTitle(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("FileGroupDescriptorW") && data.GetData("FileGroupDescriptorW") is MemoryStream ms)
            {
                // FILEGROUPDESCRIPTORW：UINT cItems + FILEDESCRIPTORW[]，檔名位於每個描述的第 72 位元組，長度 260 個 WCHAR
                var bytes = ms.ToArray();
                const int nameOffset = 4 + 72;
                if (bytes.Length >= nameOffset + 2)
                {
                    var len = Math.Min(520, bytes.Length - nameOffset);
                    var name = Encoding.Unicode.GetString(bytes, nameOffset, len);
                    var end = name.IndexOf('\0');
                    if (end >= 0)
                    {
                        name = name[..end];
                    }
                    if (name.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                    {
                        name = name[..^4];
                    }
                    return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
                }
            }
        }
        catch
        {
        }
        return null;
    }

    public static bool CanAccept(IDataObject data, BookmarkStore store) =>
        GetNodes(data, store) != null || GetPage(data) != null;

    /// <summary>
    /// 把拖曳的內容放到 parent 的 index 位置：書籤就移動，網頁就新增書籤。
    /// </summary>
    public static bool Drop(IDataObject data, BookmarkStore store, BookmarkNode parent, int index)
    {
        var nodes = GetNodes(data, store);
        if (nodes != null)
        {
            store.MoveMany(nodes, parent, index);
            return true;
        }
        if (GetPage(data) is { } page)
        {
            store.AddUrl(parent, index, page.Title, page.Url);
            return true;
        }
        return false;
    }

    public static bool IsDragGesture(Point start, Point now) =>
        Math.Abs(now.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance
        || Math.Abs(now.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;
}

/// <summary>拖放時的位置提示：插入線或反白框。</summary>
public sealed class DropIndicatorAdorner : Adorner
{
    private readonly Pen _pen;
    private readonly Brush _fill;
    private Rect? _box;
    private (Point A, Point B)? _line;

    private DropIndicatorAdorner(UIElement adorned) : base(adorned)
    {
        IsHitTestVisible = false;
        var accent = (Brush)Application.Current.FindResource("AccentBrush");
        _pen = new Pen(accent, 2);
        _pen.Freeze();
        _fill = ((Brush)Application.Current.FindResource("AccentSoftBrush")).Clone();
        _fill.Opacity = 0.6;
        _fill.Freeze();
    }

    public static DropIndicatorAdorner? Attach(UIElement element)
    {
        var layer = AdornerLayer.GetAdornerLayer(element);
        if (layer == null)
        {
            return null;
        }
        var a = new DropIndicatorAdorner(element);
        layer.Add(a);
        return a;
    }

    public void Detach() => AdornerLayer.GetAdornerLayer(AdornedElement)?.Remove(this);

    public void ShowLine(Point a, Point b)
    {
        _line = (a, b);
        _box = null;
        InvalidateVisual();
    }

    public void ShowBox(Rect r)
    {
        _box = r;
        _line = null;
        InvalidateVisual();
    }

    public void Clear()
    {
        _box = null;
        _line = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_box is { } r)
        {
            dc.DrawRoundedRectangle(_fill, _pen, r, 6, 6);
        }
        else if (_line is { } l)
        {
            dc.DrawLine(_pen, l.A, l.B);
        }
    }
}
