using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ExtHost.Services;

namespace ExtHost.Bookmarks;

/// <summary>
/// 書籤列面板：由左到右排列書籤，放不下的藏起來，最後一個子元素是「»」按鈕，只有放不下時才顯示在最右邊。
/// </summary>
public sealed class BookmarkBarPanel : Panel
{
    /// <summary>目前顯示在書籤列上的書籤數量（其餘放在「»」選單）。</summary>
    public int VisibleCount { get; private set; }

    private static readonly Rect Hidden = new(-100000, 0, 0, 0);

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = 0.0;
        var h = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            w += child.DesiredSize.Width;
            h = Math.Max(h, child.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? w : Math.Min(w, availableSize.Width), h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var n = InternalChildren.Count;
        if (n == 0)
        {
            VisibleCount = 0;
            return finalSize;
        }
        var chevron = InternalChildren[n - 1];
        var itemCount = n - 1;
        var total = 0.0;
        for (var i = 0; i < itemCount; i++)
        {
            total += InternalChildren[i].DesiredSize.Width;
        }

        var overflow = total > finalSize.Width;
        var limit = overflow ? finalSize.Width - chevron.DesiredSize.Width : finalSize.Width;
        var x = 0.0;
        var visible = 0;
        var full = false;
        for (var i = 0; i < itemCount; i++)
        {
            var child = InternalChildren[i];
            var cw = child.DesiredSize.Width;
            if (!full && x + cw <= limit)
            {
                child.Arrange(new Rect(x, (finalSize.Height - child.DesiredSize.Height) / 2, cw, child.DesiredSize.Height));
                x += cw;
                visible++;
            }
            else
            {
                full = true;
                child.Arrange(Hidden);
            }
        }
        VisibleCount = visible;

        if (overflow)
        {
            var cw = chevron.DesiredSize.Width;
            chevron.Arrange(new Rect(Math.Max(0, finalSize.Width - cw), (finalSize.Height - chevron.DesiredSize.Height) / 2, cw, chevron.DesiredSize.Height));
        }
        else
        {
            chevron.Arrange(Hidden);
        }
        return finalSize;
    }
}

/// <summary>書籤列（工具列下方），外觀與操作仿照 Chrome。</summary>
public sealed class BookmarkBar : Border
{
    private readonly BookmarkBarPanel _panel;
    private readonly StackPanel _emptyPrompt;
    private readonly Grid _otherHost;
    private readonly Button _otherButton;
    private readonly Button _chevron;
    private readonly DispatcherTimer _rebuildTimer;

    private IBookmarkHost? _host;
    private ContextMenu? _openMenu;
    private Button? _openMenuButton;
    private Button? _pendingSwitch;
    private bool _dirty;

    public BookmarkBar()
    {
        Background = Res<Brush>("SurfaceBrush");
        BorderBrush = Res<Brush>("BorderBrush");
        BorderThickness = new Thickness(0, 0, 0, 1);
        Height = 34;
        ClipToBounds = true;

        var grid = new Grid { Margin = new Thickness(8, 0, 8, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _panel = new BookmarkBarPanel { ClipToBounds = true, VerticalAlignment = VerticalAlignment.Stretch };
        grid.Children.Add(_panel);

        _chevron = new Button
        {
            Style = Res<Style>("BookmarkButton"),
            Content = new TextBlock { Text = "»", FontSize = 16, Margin = new Thickness(0, -3, 0, 0), VerticalAlignment = VerticalAlignment.Center },
            ToolTip = "更多書籤",
            Margin = new Thickness(0),
        };
        _chevron.Click += (_, _) => ToggleMenu(_chevron);

        // 書籤列沒有書籤時的提示（與 Chrome 相同）
        _emptyPrompt = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _emptyPrompt.Children.Add(new TextBlock
        {
            Text = "如要快速存取，請將書籤放在書籤列上。",
            FontSize = 12,
            Foreground = Res<Brush>("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var importLink = new TextBlock
        {
            Text = "立即匯入書籤…",
            FontSize = 12,
            Foreground = Res<Brush>("AccentBrush"),
            Cursor = Cursors.Hand,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        importLink.MouseEnter += (_, _) => importLink.TextDecorations = TextDecorations.Underline;
        importLink.MouseLeave += (_, _) => importLink.TextDecorations = null;
        importLink.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _host?.ShowImportBookmarksDialog();
        };
        _emptyPrompt.Children.Add(importLink);
        grid.Children.Add(_emptyPrompt);

        // 右側「其他書籤」
        _otherHost = new Grid();
        _otherHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _otherHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_otherHost, 1);
        _otherHost.Children.Add(new Rectangle
        {
            Width = 1,
            Height = 18,
            Fill = Res<Brush>("BorderStrongBrush"),
            Margin = new Thickness(4, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        _otherButton = new Button { Style = Res<Style>("BookmarkButton"), Margin = new Thickness(0) };
        Grid.SetColumn(_otherButton, 1);
        _otherButton.Click += (_, _) => ToggleMenu(_otherButton);
        _otherHost.Children.Add(_otherButton);
        grid.Children.Add(_otherHost);

        Child = grid;

        // 在空白處按右鍵
        MouseRightButtonUp += (_, e) =>
        {
            if (_host == null || e.Handled)
            {
                return;
            }
            e.Handled = true;
            BookmarkUi.ShowContextMenu(_host, null, _host.Bookmarks.BookmarkBar, -1, this);
        };

        _rebuildTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _rebuildTimer.Tick += (_, _) =>
        {
            _rebuildTimer.Stop();
            Rebuild();
        };
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    public void Initialize(IBookmarkHost host)
    {
        _host = host;
        host.Bookmarks.Changed += (_, _) =>
        {
            CloseMenu();
            Rebuild();
        };
        FaviconCache.Changed += (_, _) => RequestRebuild();
        Rebuild();
    }

    /// <summary>網站圖示更新時延後重建；有選單開著的話等選單關閉再重建，避免選單突然消失。</summary>
    private void RequestRebuild()
    {
        if (_openMenu != null)
        {
            _dirty = true;
            return;
        }
        _rebuildTimer.Stop();
        _rebuildTimer.Start();
    }

    public void Rebuild()
    {
        _dirty = false;
        if (_host == null)
        {
            return;
        }
        var store = _host.Bookmarks;

        _panel.Children.Clear();
        foreach (var node in store.BookmarkBar.Children)
        {
            _panel.Children.Add(MakeNodeButton(node));
        }
        _panel.Children.Add(_chevron);

        _emptyPrompt.Visibility = store.BookmarkBar.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 「其他書籤」只有裡面有東西時才顯示（與 Chrome 相同）
        _otherHost.Visibility = store.Other.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _otherButton.Content = MakeButtonContent(store.Other);
        _otherButton.Tag = store.Other;
        _otherButton.ToolTip = store.Other.Name;
        _otherButton.MouseRightButtonUp -= OtherButton_RightClick;
        _otherButton.MouseRightButtonUp += OtherButton_RightClick;
    }

    private void OtherButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (_host == null)
        {
            return;
        }
        e.Handled = true;
        BookmarkUi.ShowContextMenu(_host, _host.Bookmarks.Other, _host.Bookmarks.Other, -1, _otherButton);
    }

    private static StackPanel MakeButtonContent(BookmarkNode node)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(BookmarkUi.MakeIcon(node));
        // 沒有名稱的網址書籤只顯示圖示（與 Chrome 相同）
        if (node.IsFolder || node.Name.Length > 0)
        {
            sp.Children.Add(new TextBlock
            {
                Text = node.Name,
                Margin = new Thickness(6, 0, 0, 0),
                MaxWidth = 150,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        return sp;
    }

    private Button MakeNodeButton(BookmarkNode node)
    {
        var b = new Button
        {
            Style = Res<Style>("BookmarkButton"),
            Content = MakeButtonContent(node),
            Tag = node,
            ToolTip = BookmarkUi.ToolTipFor(node),
        };
        if (!node.IsFolder && node.Name.Length == 0)
        {
            b.Padding = new Thickness(6, 0, 6, 0);
        }

        if (node.IsFolder)
        {
            b.Click += (_, _) => ToggleMenu(b);
        }
        else
        {
            b.Click += (_, _) => _host?.OpenUrl(node.Url!, BookmarkUi.DispositionFor(MouseButton.Left));
            b.MouseUp += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.Middle)
                {
                    e.Handled = true;
                    _host?.OpenUrl(node.Url!, OpenDisposition.NewBackgroundTab);
                }
            };
        }
        b.MouseRightButtonUp += (_, e) =>
        {
            if (_host == null)
            {
                return;
            }
            e.Handled = true;
            BookmarkUi.ShowContextMenu(_host, node, node.Parent!, node.IndexInParent + 1, b);
        };
        return b;
    }

    // ======================= 下拉選單 =======================

    private void ToggleMenu(Button button)
    {
        if (_openMenuButton == button)
        {
            CloseMenu();
            return;
        }
        OpenMenu(button);
    }

    private void OpenMenu(Button button)
    {
        if (_host == null)
        {
            return;
        }
        CloseMenu();

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            MaxHeight = SystemParameters.WorkArea.Height - 80,
        };

        if (button == _chevron)
        {
            // 「»」：列出放不下的書籤
            var hidden = _host.Bookmarks.BookmarkBar.Children.Skip(_panel.VisibleCount);
            BookmarkUi.FillMenu(menu, hidden, _host, _host.Bookmarks.BookmarkBar);
        }
        else if (button.Tag is BookmarkNode folder)
        {
            BookmarkUi.FillMenu(menu, folder.Children, _host, folder);
        }

        _openMenu = menu;
        _openMenuButton = button;
        button.Background = Res<Brush>("PressedBrush");

        menu.PreviewMouseMove += Menu_PreviewMouseMove;
        menu.Closed += (_, _) =>
        {
            button.ClearValue(BackgroundProperty);
            if (_openMenu == menu)
            {
                _openMenu = null;
                // 延後清除，讓「再按一次同一個資料夾 = 關閉」可以判斷
                Dispatcher.BeginInvoke(() =>
                {
                    if (_openMenu == null)
                    {
                        _openMenuButton = null;
                    }
                }, DispatcherPriority.Input);
                if (_dirty)
                {
                    RequestRebuild();
                }
            }
        };
        menu.IsOpen = true;
    }

    private void CloseMenu()
    {
        if (_openMenu != null)
        {
            var m = _openMenu;
            _openMenu = null;
            _openMenuButton = null;
            m.IsOpen = false;
        }
    }

    /// <summary>選單開著時，滑鼠移到書籤列上另一個資料夾就直接切換過去（與 Chrome 相同）。</summary>
    private void Menu_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_openMenu == null)
        {
            return;
        }
        var target = FolderButtonAt(e);
        if (target != null && target != _openMenuButton && target != _pendingSwitch)
        {
            _pendingSwitch = target;
            Dispatcher.BeginInvoke(() =>
            {
                _pendingSwitch = null;
                if (_openMenu != null)
                {
                    OpenMenu(target);
                }
            }, DispatcherPriority.Input);
        }
    }

    private Button? FolderButtonAt(MouseEventArgs e)
    {
        IEnumerable<Button> candidates = _panel.Children.OfType<Button>()
            .Take(_panel.VisibleCount)
            .Where(b => b.Tag is BookmarkNode { IsFolder: true });
        if (_panel.VisibleCount < _host!.Bookmarks.BookmarkBar.Children.Count)
        {
            candidates = candidates.Append(_chevron);
        }
        if (_otherHost.Visibility == Visibility.Visible)
        {
            candidates = candidates.Append(_otherButton);
        }

        foreach (var b in candidates)
        {
            if (!b.IsVisible)
            {
                continue;
            }
            var p = e.GetPosition(b);
            if (p.X >= 0 && p.Y >= 0 && p.X < b.ActualWidth && p.Y < b.ActualHeight)
            {
                return b;
            }
        }
        return null;
    }
}
