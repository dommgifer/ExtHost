using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ExtHost.Services;

namespace ExtHost.Bookmarks;

public enum OpenDisposition
{
    CurrentTab,
    NewForegroundTab,
    NewBackgroundTab,
}

/// <summary>書籤 UI 需要主視窗提供的功能。</summary>
public interface IBookmarkHost
{
    BookmarkStore Bookmarks { get; }

    Window OwnerWindow { get; }

    /// <summary>「一律顯示書籤列」是否開啟。</summary>
    bool IsBookmarkBarPinned { get; }

    void OpenUrl(string url, OpenDisposition disposition);

    /// <summary>「全部開啟」：第一個切換過去，其他在背景開啟。</summary>
    void OpenUrls(IReadOnlyList<string> urls);

    void ToggleBookmarkBar();

    void ShowImportBookmarksDialog();
}

/// <summary>書籤的圖示、選單等共用 UI。</summary>
public static class BookmarkUi
{
    public const string FolderGlyph = "";
    public const string PageGlyph = "";
    public const string StarGlyph = "";
    public const string StarFillGlyph = "";

    /// <summary>Chrome 在「全部開啟」超過這個數量時會先詢問。</summary>
    private const int OpenAllConfirmThreshold = 15;

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    /// <summary>16×16 的書籤圖示：網站 favicon、預設地球圖示或資料夾圖示。</summary>
    public static FrameworkElement MakeIcon(BookmarkNode node)
    {
        if (!node.IsFolder && FaviconCache.Get(node.Url) is { } img)
        {
            return new Image
            {
                Source = img,
                Width = 16,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
            }.WithScaling();
        }
        return MakeGlyph(node.IsFolder ? FolderGlyph : PageGlyph);
    }

    public static TextBlock MakeGlyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = Res<FontFamily>("IconFont"),
        FontSize = 14,
        Width = 16,
        Height = 16,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Res<Brush>("MutedBrush"),
    };

    private static Image WithScaling(this Image img)
    {
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    public static string ToolTipFor(BookmarkNode node) =>
        node.IsFolder ? node.Name
        : node.Name.Length == 0 ? node.Url ?? ""
        : node.Name + "\n" + node.Url;

    /// <summary>依滑鼠按鍵與 Ctrl / Shift 決定開啟方式（與 Chrome 相同）。</summary>
    public static OpenDisposition DispositionFor(MouseButton button)
    {
        var mods = Keyboard.Modifiers;
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var shift = mods.HasFlag(ModifierKeys.Shift);
        if (button == MouseButton.Middle || ctrl)
        {
            return shift ? OpenDisposition.NewForegroundTab : OpenDisposition.NewBackgroundTab;
        }
        return shift ? OpenDisposition.NewForegroundTab : OpenDisposition.CurrentTab;
    }

    private static object HeaderText(string text) => new TextBlock
    {
        Text = text,
        MaxWidth = 320,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    // ======================= 資料夾下拉選單 =======================

    /// <summary>把節點清單加到選單（資料夾會變成子選單，展開時才建立內容）。</summary>
    public static void FillMenu(ItemsControl menu, IEnumerable<BookmarkNode> nodes, IBookmarkHost host, BookmarkNode folderForEmpty)
    {
        var any = false;
        foreach (var node in nodes)
        {
            any = true;
            menu.Items.Add(MakeNodeMenuItem(node, host));
        }
        if (!any)
        {
            var empty = new MenuItem { Header = "（空白）", IsEnabled = false };
            empty.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                ShowContextMenu(host, null, folderForEmpty, -1, (UIElement)s);
            };
            menu.Items.Add(empty);
        }
    }

    private static MenuItem MakeNodeMenuItem(BookmarkNode node, IBookmarkHost host)
    {
        var mi = new MenuItem
        {
            Header = HeaderText(node.DisplayName),
            Icon = MakeIcon(node),
            Tag = node,
        };
        if (!node.IsFolder)
        {
            mi.ToolTip = node.Url;
        }

        if (node.IsFolder)
        {
            // 子選單在展開時才建立，匯入大量書籤時才不會卡
            mi.Items.Add(new MenuItem { Header = "…", IsEnabled = false });
            var built = false;
            mi.SubmenuOpened += (_, e) =>
            {
                if (built || e.OriginalSource != mi)
                {
                    return;
                }
                built = true;
                mi.Items.Clear();
                FillMenu(mi, node.Children, host, node);
            };
        }
        else
        {
            mi.Click += (_, e) =>
            {
                if (e.OriginalSource == mi)
                {
                    host.OpenUrl(node.Url!, DispositionFor(MouseButton.Left));
                }
            };
            mi.PreviewMouseUp += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.Middle)
                {
                    e.Handled = true;
                    CloseMenusOf(mi);
                    host.OpenUrl(node.Url!, OpenDisposition.NewBackgroundTab);
                }
            };
        }

        mi.PreviewMouseRightButtonUp += (_, e) =>
        {
            // 子選單項目的事件會經過上層選單項目，只處理滑鼠真正所在的那一個
            if (!mi.IsMouseDirectlyOverOrHeader())
            {
                return;
            }
            e.Handled = true;
            ShowContextMenu(host, node, node.Parent!, node.IndexInParent + 1, mi);
        };
        return mi;
    }

    private static bool IsMouseDirectlyOverOrHeader(this MenuItem mi)
    {
        if (!mi.IsMouseOver)
        {
            return false;
        }
        // 滑鼠在子選單的項目上時，子項目本身也會 IsMouseOver
        foreach (var child in mi.Items.OfType<MenuItem>())
        {
            if (child.IsMouseOver)
            {
                return false;
            }
        }
        return true;
    }

    private static void CloseMenusOf(DependencyObject d)
    {
        for (var p = d; p != null; p = LogicalTreeHelper.GetParent(p) ?? VisualTreeHelper.GetParent(p))
        {
            if (p is ContextMenu cm)
            {
                cm.IsOpen = false;
                return;
            }
        }
    }

    // ======================= 右鍵選單 =======================

    /// <summary>
    /// 書籤右鍵選單（與 Chrome 相同的項目）。
    /// node 為 null 表示在空白處按右鍵；新增的網頁 / 資料夾放在 addParent 的 addIndex 位置。
    /// </summary>
    public static void ShowContextMenu(IBookmarkHost host, BookmarkNode? node, BookmarkNode addParent, int addIndex, UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };

        MenuItem Item(string header, Action onClick, string? gesture = null, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
            mi.Click += (_, _) => onClick();
            return mi;
        }

        if (node != null && !node.IsFolder)
        {
            menu.Items.Add(Item("在新分頁中開啟", () => host.OpenUrl(node.Url!, OpenDisposition.NewBackgroundTab)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("編輯…", () => BookmarkEditorWindow.EditUrl(host, node)));
            menu.Items.Add(Item("複製", () => CopyText(node.Url!)));
            menu.Items.Add(Item("刪除", () => host.Bookmarks.Remove(node)));
            menu.Items.Add(new Separator());
        }
        else if (node != null)
        {
            var urls = node.Children.Where(c => !c.IsFolder).Select(c => c.Url!).ToList();
            menu.Items.Add(Item(urls.Count > 0 ? $"全部開啟 ({urls.Count})" : "全部開啟", () => OpenAll(host, urls), enabled: urls.Count > 0));
            menu.Items.Add(new Separator());
            if (!node.IsRoot)
            {
                menu.Items.Add(Item("重新命名…", () => BookmarkEditorWindow.EditFolder(host, node)));
                menu.Items.Add(Item("刪除", () => DeleteFolder(host, node)));
                menu.Items.Add(new Separator());
            }
        }

        menu.Items.Add(Item("新增網頁…", () => BookmarkEditorWindow.AddUrl(host, addParent, addIndex)));
        menu.Items.Add(Item("新增資料夾…", () => BookmarkEditorWindow.AddFolder(host, addParent, addIndex)));
        menu.Items.Add(new Separator());
        var show = new MenuItem { Header = "顯示書籤列", IsCheckable = true, IsChecked = host.IsBookmarkBarPinned, InputGestureText = "Ctrl+Shift+B" };
        show.Click += (_, _) => host.ToggleBookmarkBar();
        menu.Items.Add(show);

        menu.IsOpen = true;
    }

    public static void OpenAll(IBookmarkHost host, IReadOnlyList<string> urls)
    {
        if (urls.Count == 0)
        {
            return;
        }
        if (urls.Count > OpenAllConfirmThreshold
            && MessageBox.Show(host.OwnerWindow, $"確定要開啟 {urls.Count} 個分頁嗎？", "ExtHost",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }
        host.OpenUrls(urls);
    }

    private static void DeleteFolder(IBookmarkHost host, BookmarkNode folder)
    {
        var count = folder.Descendants().Count(n => !n.IsFolder);
        if (count > 0
            && MessageBox.Show(host.OwnerWindow, $"確定要刪除「{folder.Name}」資料夾和其中的 {count} 個書籤嗎？", "刪除資料夾",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        host.Bookmarks.Remove(folder);
    }

    private static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // 剪貼簿被其他程式佔用
        }
    }
}
