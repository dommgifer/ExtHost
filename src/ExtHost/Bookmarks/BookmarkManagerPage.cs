using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using ExtHost.Services;

namespace ExtHost.Bookmarks;

/// <summary>書籤管理員清單中的一列。</summary>
public sealed class BookmarkRow
{
    public BookmarkRow(BookmarkNode node, bool showUrl)
    {
        Node = node;
        Icon = node.IsFolder ? null : FaviconCache.Get(node.Url);
        Url = showUrl && !node.IsFolder ? node.Url ?? "" : "";
    }

    public BookmarkNode Node { get; }
    public ImageSource? Icon { get; }
    public bool HasIcon => Icon != null;
    public string Glyph => Node.IsFolder ? BookmarkUi.FolderGlyph : BookmarkUi.PageGlyph;
    public string Name => Node.DisplayName;
    public string Url { get; }
}

/// <summary>
/// 書籤管理員（Ctrl+Shift+O），版面仿照 Chrome 的 chrome://bookmarks：
/// 上方標題與搜尋框、左側資料夾樹、右側書籤清單，支援多選、拖放、刪除後復原。
/// </summary>
public sealed class BookmarkManagerPage : UserControl
{
    private const string RowTemplateXaml = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Grid Height="36" Background="Transparent">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="44" />
              <ColumnDefinition Width="3*" />
              <ColumnDefinition Width="4*" />
              <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <Grid Width="16" Height="16" HorizontalAlignment="Center" VerticalAlignment="Center">
              <Image Source="{Binding Icon}" Visibility="{Binding HasIcon, Converter={StaticResource BoolToVis}}" RenderOptions.BitmapScalingMode="HighQuality" />
              <TextBlock Text="{Binding Glyph}" FontFamily="{StaticResource IconFont}" FontSize="14" Foreground="{StaticResource MutedBrush}"
                         HorizontalAlignment="Center" VerticalAlignment="Center"
                         Visibility="{Binding HasIcon, Converter={StaticResource InverseBoolToVis}}" />
            </Grid>
            <TextBlock Grid.Column="1" Text="{Binding Name}" FontSize="13" Foreground="{StaticResource TextBrush}"
                       VerticalAlignment="Center" TextTrimming="CharacterEllipsis" Margin="0,0,16,0" />
            <TextBlock Grid.Column="2" Text="{Binding Url}" FontSize="12.5" Foreground="{StaticResource SubtleBrush}"
                       VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
            <Button Grid.Column="3" Content="&#xE712;" Style="{StaticResource IconButton}" Width="32" Height="32" FontSize="13"
                    Margin="8,0,4,0" Tag="{Binding}" ToolTip="更多動作" />
          </Grid>
        </DataTemplate>
        """;

    private const string RowContainerStyleXaml = """
        <Style TargetType="ListBoxItem" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Setter Property="FocusVisualStyle" Value="{x:Null}" />
          <Setter Property="HorizontalContentAlignment" Value="Stretch" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ListBoxItem">
                <Border x:Name="Bd" Background="Transparent" CornerRadius="6" Margin="6,1">
                  <ContentPresenter />
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="Bd" Property="Background" Value="{StaticResource BgBrush}" />
                  </Trigger>
                  <Trigger Property="IsSelected" Value="True">
                    <Setter TargetName="Bd" Property="Background" Value="{StaticResource AccentSoftBrush}" />
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;

    private readonly IBookmarkHost _host;
    private readonly BookmarkStore _store;

    private readonly TextBox _search;
    private readonly TextBlock _searchPlaceholder;
    private readonly Border _header;
    private readonly Border _selectionBar;
    private readonly TextBlock _selectionText;
    private readonly TreeView _tree;
    private readonly ListBox _list;
    private readonly TextBlock _emptyText;
    private readonly Border _toast;
    private readonly TextBlock _toastText;
    private readonly ObservableCollection<BookmarkRow> _rows = new();
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _toastTimer;

    private BookmarkNode _folder;
    private string _query = "";
    private bool _suppressTreeSelection;

    // 拖曳
    private Point _dragStart;
    private BookmarkRow? _pressedRow;
    private bool _deferSelect;
    private DropIndicatorAdorner? _listIndicator;
    private DropIndicatorAdorner? _treeIndicator;

    public BookmarkManagerPage(IBookmarkHost host)
    {
        _host = host;
        _store = host.Bookmarks;
        _folder = _store.BookmarkBar;

        Background = Res<Brush>("BgBrush");
        FontFamily = Res<FontFamily>("UiFont");
        Focusable = true;

        var root = new DockPanel();

        // ===== 標題列（標題、搜尋、整理選單） =====
        var headerGrid = new Grid { Height = 56, Margin = new Thickness(20, 0, 12, 0) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MaxWidth = 680 });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock
        {
            Text = BookmarkUi.StarFillGlyph,
            FontFamily = Res<FontFamily>("IconFont"),
            FontSize = 18,
            Foreground = Res<Brush>("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        title.Children.Add(new TextBlock { Text = "書籤", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
        headerGrid.Children.Add(title);

        var searchBorder = new Border
        {
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Background = Res<Brush>("AddressBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
        };
        Grid.SetColumn(searchBorder, 1);
        var searchGrid = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchGrid.Children.Add(new TextBlock
        {
            Text = "",
            FontFamily = Res<FontFamily>("IconFont"),
            FontSize = 14,
            Foreground = Res<Brush>("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        _search = new TextBox { Style = Res<Style>("AddressTextBox") };
        Grid.SetColumn(_search, 1);
        _searchPlaceholder = new TextBlock
        {
            Text = "搜尋書籤",
            FontSize = 14,
            Foreground = Res<Brush>("SubtleBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        Grid.SetColumn(_searchPlaceholder, 1);
        searchGrid.Children.Add(_searchPlaceholder);
        searchGrid.Children.Add(_search);
        searchBorder.Child = searchGrid;
        headerGrid.Children.Add(searchBorder);

        var organize = new Button
        {
            Style = Res<Style>("IconButton"),
            Content = "",
            ToolTip = "整理",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        organize.Click += (_, _) => ShowOrganizeMenu(organize);
        Grid.SetColumn(organize, 2);
        headerGrid.Children.Add(organize);

        _header = new Border
        {
            Background = Res<Brush>("SurfaceBrush"),
            BorderBrush = Res<Brush>("BorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerGrid,
        };

        // ===== 選取項目時的工具列（與 Chrome 相同，蓋在標題列上） =====
        var selGrid = new DockPanel { Height = 56, Margin = new Thickness(12, 0, 16, 0), LastChildFill = false };
        var clearSel = new Button { Style = Res<Style>("IconButton"), Content = "", ToolTip = "取消選取", FontSize = 13 };
        clearSel.Click += (_, _) => _list!.UnselectAll();
        selGrid.Children.Add(clearSel);
        _selectionText = new TextBlock { FontSize = 15, Foreground = Res<Brush>("AccentTextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        selGrid.Children.Add(_selectionText);
        var delSel = new Button { Content = "刪除", Style = Res<Style>("SecondaryButton") };
        delSel.Click += (_, _) => DeleteNodes(SelectedNodes());
        DockPanel.SetDock(delSel, Dock.Right);
        selGrid.Children.Add(delSel);
        _selectionBar = new Border
        {
            Background = Res<Brush>("AccentSoftBrush"),
            BorderBrush = Res<Brush>("BorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = selGrid,
            Visibility = Visibility.Collapsed,
        };

        var top = new Grid();
        top.Children.Add(_header);
        top.Children.Add(_selectionBar);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        // ===== 內容：左側資料夾樹、右側清單 =====
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _tree = new TreeView
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 10, 8, 10),
            AllowDrop = true,
        };
        _tree.SelectedItemChanged += Tree_SelectedItemChanged;
        _tree.DragOver += Tree_DragOver;
        _tree.DragEnter += Tree_DragOver;
        _tree.DragLeave += (_, _) => _treeIndicator?.Clear();
        _tree.Drop += Tree_Drop;
        _tree.PreviewMouseRightButtonUp += Tree_RightClick;
        body.Children.Add(_tree);

        var card = new Border
        {
            Background = Res<Brush>("SurfaceBrush"),
            BorderBrush = Res<Brush>("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(8, 16, 24, 16),
            MaxWidth = 960,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetColumn(card, 1);
        var cardGrid = new Grid();
        _list = new ListBox
        {
            SelectionMode = SelectionMode.Extended,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 6, 0, 6),
            ItemsSource = _rows,
            ItemTemplate = (DataTemplate)XamlReader.Parse(RowTemplateXaml),
            ItemContainerStyle = (Style)XamlReader.Parse(RowContainerStyleXaml),
            AllowDrop = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        _list.SelectionChanged += (_, _) => UpdateSelectionBar();
        _list.MouseDoubleClick += List_DoubleClick;
        _list.PreviewMouseLeftButtonDown += List_PreviewMouseLeftButtonDown;
        _list.PreviewMouseLeftButtonUp += List_PreviewMouseLeftButtonUp;
        _list.PreviewMouseMove += List_PreviewMouseMove;
        _list.MouseUp += List_MouseUp;
        _list.PreviewMouseRightButtonUp += List_RightClick;
        _list.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(RowMoreButton_Click));
        _list.DragEnter += List_DragOver;
        _list.DragOver += List_DragOver;
        _list.DragLeave += (_, _) => _listIndicator?.Clear();
        _list.Drop += List_Drop;
        _list.PreviewKeyDown += List_KeyDown;
        cardGrid.Children.Add(_list);

        _emptyText = new TextBlock
        {
            FontSize = 13,
            Foreground = Res<Brush>("SubtleBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 40, 0, 40),
            IsHitTestVisible = false,
        };
        cardGrid.Children.Add(_emptyText);
        card.Child = cardGrid;
        body.Children.Add(card);

        // ===== 刪除後的「復原」提示 =====
        var toastPanel = new StackPanel { Orientation = Orientation.Horizontal };
        _toastText = new TextBlock { Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420, TextTrimming = TextTrimming.CharacterEllipsis };
        toastPanel.Children.Add(_toastText);
        var undo = new TextBlock
        {
            Text = "復原",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xB4, 0xF8)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(24, 0, 0, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        undo.MouseLeftButtonUp += (_, _) => Undo();
        toastPanel.Children.Add(undo);
        _toast = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x32, 0x32, 0x32)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(18, 12, 18, 12),
            Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = toastPanel,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetColumnSpan(_toast, 2);
        body.Children.Add(_toast);

        root.Children.Add(body);
        Content = root;

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            _query = _search.Text.Trim();
            RefreshList();
            UpdateTreeSelection();
        };
        _search.TextChanged += (_, _) =>
        {
            _searchPlaceholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _searchTimer.Stop();
            _searchTimer.Start();
        };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _search.Text.Length > 0)
            {
                _search.Clear();
                e.Handled = true;
            }
        };

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toast.Visibility = Visibility.Collapsed;
        };

        PreviewKeyDown += Page_KeyDown;
        _store.Changed += OnStoreChanged;
        FaviconCache.Changed += OnFaviconsChanged;

        Refresh();
    }

    private void OnStoreChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);

    private void OnFaviconsChanged(object? sender, EventArgs e)
    {
        if (IsVisible)
        {
            RefreshList();
        }
    }

    /// <summary>分頁關閉時取消訂閱。</summary>
    public void Detach()
    {
        _store.Changed -= OnStoreChanged;
        FaviconCache.Changed -= OnFaviconsChanged;
        _searchTimer.Stop();
        _toastTimer.Stop();
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    private bool IsSearching => _query.Length > 0;

    // ======================= 顯示 =======================

    public void Refresh()
    {
        if (!_store.IsAttached(_folder))
        {
            _folder = _store.BookmarkBar;
        }
        RebuildTree();
        RefreshList();
    }

    /// <summary>切換到指定資料夾（清除搜尋），可選擇同時選取某個項目。</summary>
    public void ShowFolder(BookmarkNode folder, BookmarkNode? select = null)
    {
        if (!folder.IsFolder || !_store.IsAttached(folder))
        {
            return;
        }
        _folder = folder;
        if (_search.Text.Length > 0)
        {
            _search.Text = "";
            _searchTimer.Stop();
        }
        _query = "";
        RefreshList();
        UpdateTreeSelection();
        if (select != null && _rows.FirstOrDefault(r => r.Node == select) is { } row)
        {
            _list.SelectedItem = row;
            _list.ScrollIntoView(row);
        }
    }

    private void RefreshList()
    {
        var selected = new HashSet<long>(SelectedNodes().Select(n => n.Id));
        _rows.Clear();
        IEnumerable<BookmarkNode> nodes = IsSearching
            ? _store.AllNodes().Where(n => !n.IsFolder
                && (n.Name.Contains(_query, StringComparison.CurrentCultureIgnoreCase)
                    || (n.Url?.Contains(_query, StringComparison.OrdinalIgnoreCase) ?? false)))
            : _folder.Children;
        foreach (var n in nodes)
        {
            _rows.Add(new BookmarkRow(n, true));
        }
        foreach (var r in _rows.Where(r => selected.Contains(r.Node.Id)))
        {
            _list.SelectedItems.Add(r);
        }
        _emptyText.Text = IsSearching ? "找不到任何搜尋結果" : "這個資料夾中沒有任何書籤";
        _emptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionBar();
    }

    private void UpdateSelectionBar()
    {
        var n = _list.SelectedItems.Count;
        _selectionBar.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        _selectionText.Text = $"已選取 {n} 個項目";
    }

    private List<BookmarkNode> SelectedNodes() =>
        _rows.Where(r => _list.SelectedItems.Contains(r)).Select(r => r.Node).ToList();

    // ======================= 資料夾樹 =======================

    private readonly HashSet<long> _expanded = new() { 1, 2 };

    private void RebuildTree()
    {
        _suppressTreeSelection = true;
        _tree.Items.Clear();
        foreach (var r in _store.Roots)
        {
            _tree.Items.Add(MakeTreeItem(r));
        }
        _suppressTreeSelection = false;
        UpdateTreeSelection();
    }

    private TreeViewItem MakeTreeItem(BookmarkNode folder)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 8, 3) };
        header.Children.Add(BookmarkUi.MakeGlyph(BookmarkUi.FolderGlyph));
        header.Children.Add(new TextBlock
        {
            Text = folder.Name,
            FontSize = 13,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 180,
        });
        var item = new TreeViewItem
        {
            Header = header,
            Tag = folder,
            IsExpanded = _expanded.Contains(folder.Id),
            ToolTip = folder.Name,
        };
        item.Expanded += (_, e) => { if (e.OriginalSource == item) { _expanded.Add(folder.Id); } };
        item.Collapsed += (_, e) => { if (e.OriginalSource == item) { _expanded.Remove(folder.Id); } };
        foreach (var c in folder.Children.Where(c => c.IsFolder))
        {
            item.Items.Add(MakeTreeItem(c));
        }
        return item;
    }

    private IEnumerable<TreeViewItem> AllTreeItems()
    {
        IEnumerable<TreeViewItem> Walk(ItemsControl parent)
        {
            foreach (var i in parent.Items.OfType<TreeViewItem>())
            {
                yield return i;
                foreach (var c in Walk(i))
                {
                    yield return c;
                }
            }
        }
        return Walk(_tree);
    }

    private void UpdateTreeSelection()
    {
        _suppressTreeSelection = true;
        try
        {
            if (IsSearching)
            {
                if (_tree.SelectedItem is TreeViewItem sel)
                {
                    sel.IsSelected = false;
                }
                return;
            }
            // 展開到目前資料夾
            for (var p = _folder.Parent; p != null; p = p.Parent)
            {
                _expanded.Add(p.Id);
            }
            foreach (var item in AllTreeItems())
            {
                var f = (BookmarkNode)item.Tag;
                if (_expanded.Contains(f.Id) && !item.IsExpanded)
                {
                    item.IsExpanded = true;
                }
                if (f == _folder)
                {
                    item.IsSelected = true;
                    item.BringIntoView();
                }
            }
        }
        finally
        {
            _suppressTreeSelection = false;
        }
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeSelection || (_tree.SelectedItem as TreeViewItem)?.Tag is not BookmarkNode folder)
        {
            return;
        }
        ShowFolder(folder);
    }

    private static TreeViewItem? TreeItemFrom(DependencyObject? d)
    {
        while (d != null && d is not TreeViewItem)
        {
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return d as TreeViewItem;
    }

    private void Tree_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (TreeItemFrom(e.OriginalSource as DependencyObject)?.Tag is not BookmarkNode folder)
        {
            return;
        }
        e.Handled = true;
        ShowFolderMenu(folder);
    }

    private void ShowFolderMenu(BookmarkNode folder)
    {
        var menu = NewMenu();
        var urls = folder.Children.Where(c => !c.IsFolder).Select(c => c.Url!).ToList();
        if (!folder.IsRoot)
        {
            menu.Items.Add(Item("重新命名…", () => BookmarkEditorWindow.EditFolder(_host, folder)));
            menu.Items.Add(Item("刪除", () => DeleteNodes(new[] { folder })));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item("新增書籤…", () => BookmarkEditorWindow.AddUrl(_host, folder, -1)));
        menu.Items.Add(Item("新增資料夾…", () => BookmarkEditorWindow.AddFolder(_host, folder, -1)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(urls.Count > 0 ? $"全部開啟 ({urls.Count})" : "全部開啟", () => BookmarkUi.OpenAll(_host, urls), enabled: urls.Count > 0));
        menu.IsOpen = true;
    }

    // ======================= 選單 =======================

    private static ContextMenu NewMenu() => new() { Placement = PlacementMode.MousePoint };

    private static MenuItem Item(string header, Action onClick, string? gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private void ShowOrganizeMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var target = IsSearching ? _store.BookmarkBar : _folder;
        menu.Items.Add(Item("新增書籤…", () => BookmarkEditorWindow.AddUrl(_host, target, -1)));
        menu.Items.Add(Item("新增資料夾…", () => BookmarkEditorWindow.AddFolder(_host, target, -1)));
        menu.Items.Add(Item("依名稱排序", () => _store.SortByName(_folder), enabled: !IsSearching && _folder.Children.Count > 1));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("匯入書籤…", _host.ShowImportBookmarksDialog));
        menu.Items.Add(Item("匯出書籤…", () => BookmarkUi.ExportWithDialog(_host)));
        menu.IsOpen = true;
    }

    /// <summary>清單項目的選單（右鍵或「⋮」）。</summary>
    private void ShowItemsMenu(IReadOnlyList<BookmarkNode> nodes, UIElement? anchor)
    {
        if (nodes.Count == 0)
        {
            return;
        }
        var menu = anchor != null
            ? new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom }
            : NewMenu();

        if (nodes.Count == 1)
        {
            var n = nodes[0];
            if (n.IsFolder)
            {
                menu.Items.Add(Item("重新命名…", () => BookmarkEditorWindow.EditFolder(_host, n)));
            }
            else
            {
                menu.Items.Add(Item("編輯…", () => BookmarkEditorWindow.EditUrl(_host, n)));
                menu.Items.Add(Item("複製網址", () => BookmarkUi.CopyText(n.Url!), "Ctrl+C"));
            }
            menu.Items.Add(Item("刪除", () => DeleteNodes(nodes), "Delete"));
            if (IsSearching && n.Parent != null)
            {
                menu.Items.Add(Item("在資料夾中顯示", () => ShowFolder(n.Parent, n)));
            }
            menu.Items.Add(new Separator());
        }
        else
        {
            menu.Items.Add(Item("複製網址", () => CopyUrls(nodes), "Ctrl+C"));
            menu.Items.Add(Item($"刪除 {nodes.Count} 個項目", () => DeleteNodes(nodes), "Delete"));
            menu.Items.Add(new Separator());
        }

        var urls = UrlsOf(nodes);
        if (nodes.Count == 1 && !nodes[0].IsFolder)
        {
            menu.Items.Add(Item("在新分頁中開啟", () => _host.OpenUrl(urls[0], OpenDisposition.NewBackgroundTab)));
        }
        else
        {
            menu.Items.Add(Item(urls.Count > 0 ? $"全部開啟 ({urls.Count})" : "全部開啟", () => BookmarkUi.OpenAll(_host, urls), enabled: urls.Count > 0));
        }
        menu.IsOpen = true;
    }

    /// <summary>選取項目中的網址（資料夾取其第一層的書籤，與 Chrome 相同）。</summary>
    private static List<string> UrlsOf(IEnumerable<BookmarkNode> nodes) =>
        nodes.SelectMany(n => n.IsFolder ? n.Children.Where(c => !c.IsFolder) : new[] { n })
            .Select(n => n.Url!)
            .ToList();

    private void CopyUrls(IEnumerable<BookmarkNode> nodes)
    {
        var urls = nodes.SelectMany(n => n.IsFolder ? n.Descendants() : new[] { n }).Where(n => !n.IsFolder).Select(n => n.Url!).ToList();
        if (urls.Count > 0)
        {
            BookmarkUi.CopyText(string.Join(Environment.NewLine, urls));
        }
    }

    // ======================= 刪除 / 復原 =======================

    private void DeleteNodes(IReadOnlyList<BookmarkNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return;
        }
        var text = nodes.Count == 1 ? $"已刪除「{nodes[0].DisplayName}」" : $"已刪除 {nodes.Count} 個項目";
        _store.RemoveMany(nodes);
        _toastText.Text = text;
        _toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void Undo()
    {
        _toast.Visibility = Visibility.Collapsed;
        _toastTimer.Stop();
        var restored = _store.UndoRemove();
        if (restored.Count > 0)
        {
            Dispatcher.BeginInvoke(() =>
            {
                foreach (var r in _rows.Where(r => restored.Contains(r.Node)))
                {
                    _list.SelectedItems.Add(r);
                }
            }, DispatcherPriority.Background);
        }
    }

    // ======================= 鍵盤 =======================

    private void Page_KeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.F)
        {
            _search.Focus();
            _search.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Z && !_search.IsKeyboardFocusWithin)
        {
            Undo();
            e.Handled = true;
        }
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var nodes = SelectedNodes();
        switch (e.Key)
        {
            case Key.Delete when nodes.Count > 0:
                DeleteNodes(nodes);
                e.Handled = true;
                break;
            case Key.Enter when nodes.Count > 0:
                OpenNodes(nodes, ctrl ? OpenDisposition.NewBackgroundTab : OpenDisposition.NewForegroundTab);
                e.Handled = true;
                break;
            case Key.C when ctrl && nodes.Count > 0:
                CopyUrls(nodes);
                e.Handled = true;
                break;
            case Key.Escape when nodes.Count > 0:
                _list.UnselectAll();
                e.Handled = true;
                break;
        }
    }

    // ======================= 滑鼠 =======================

    private BookmarkRow? RowFrom(object? source) =>
        (ItemsControl.ContainerFromElement(_list, source as DependencyObject) as ListBoxItem)?.DataContext as BookmarkRow;

    private void OpenNodes(IReadOnlyList<BookmarkNode> nodes, OpenDisposition disposition)
    {
        if (nodes.Count == 1 && nodes[0].IsFolder)
        {
            ShowFolder(nodes[0]);
            return;
        }
        var urls = UrlsOf(nodes);
        if (urls.Count == 1)
        {
            _host.OpenUrl(urls[0], disposition);
        }
        else
        {
            BookmarkUi.OpenAll(_host, urls);
        }
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || RowFrom(e.OriginalSource) is not { } row)
        {
            return;
        }
        // 管理員本身不是網頁分頁，書籤開在新分頁
        OpenNodes(new[] { row.Node }, OpenDisposition.NewForegroundTab);
        e.Handled = true;
    }

    private void List_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && RowFrom(e.OriginalSource) is { Node.IsFolder: false } row)
        {
            _host.OpenUrl(row.Node.Url!, OpenDisposition.NewBackgroundTab);
            e.Handled = true;
        }
    }

    private void List_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var row = RowFrom(e.OriginalSource);
        if (row == null)
        {
            if (IsSearching)
            {
                return;
            }
            var menu = NewMenu();
            menu.Items.Add(Item("新增書籤…", () => BookmarkEditorWindow.AddUrl(_host, _folder, -1)));
            menu.Items.Add(Item("新增資料夾…", () => BookmarkEditorWindow.AddFolder(_host, _folder, -1)));
            menu.IsOpen = true;
            return;
        }
        if (!_list.SelectedItems.Contains(row))
        {
            _list.SelectedItem = row;
        }
        ShowItemsMenu(SelectedNodes(), null);
    }

    private void RowMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Button { Tag: BookmarkRow row } b)
        {
            e.Handled = true;
            if (!_list.SelectedItems.Contains(row) || _list.SelectedItems.Count == 1)
            {
                _list.SelectedItem = row;
            }
            ShowItemsMenu(SelectedNodes(), b);
        }
    }

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(_list);
        _pressedRow = RowFrom(e.OriginalSource);
        _deferSelect = false;

        if (_pressedRow == null || IsInButton(e.OriginalSource as DependencyObject))
        {
            _pressedRow = null;
            return;
        }
        // 在已選取的項目上按下且沒有按 Ctrl / Shift：先不改變選取，才能一次拖曳多個項目
        if (_list.SelectedItems.Contains(_pressedRow) && Keyboard.Modifiers == ModifierKeys.None && e.ClickCount == 1)
        {
            _deferSelect = true;
            e.Handled = true;
            _list.Focus();
        }
    }

    private static bool IsInButton(DependencyObject? d)
    {
        while (d != null && d is not ListBoxItem)
        {
            if (d is ButtonBase)
            {
                return true;
            }
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferSelect && _pressedRow != null)
        {
            // 沒有拖曳：當成一般點擊，只選取這一個
            _list.SelectedItem = _pressedRow;
        }
        _deferSelect = false;
        _pressedRow = null;
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressedRow == null
            || !BookmarkDrag.IsDragGesture(_dragStart, e.GetPosition(_list)))
        {
            return;
        }
        if (!_list.SelectedItems.Contains(_pressedRow))
        {
            _list.SelectedItem = _pressedRow;
        }
        var nodes = SelectedNodes();
        _pressedRow = null;
        _deferSelect = false;
        if (nodes.Count == 0)
        {
            return;
        }
        try
        {
            DragDrop.DoDragDrop(_list, BookmarkDrag.ForNodes(nodes), DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch (Exception ex)
        {
            AppPaths.Log("拖曳書籤失敗：" + ex.Message);
        }
        finally
        {
            ClearIndicators();
        }
    }

    // ======================= 拖放 =======================

    /// <summary>清單上的放置目標：放進哪個資料夾的哪個位置，以及提示。</summary>
    private (BookmarkNode Parent, int Index, Rect? Box, (Point, Point)? Line)? ListDropTarget(DragEventArgs e)
    {
        var p = e.GetPosition(_list);
        var container = ItemsControl.ContainerFromElement(_list, e.OriginalSource as DependencyObject) as ListBoxItem;
        var w = _list.ActualWidth;

        if (container?.DataContext is BookmarkRow row)
        {
            var top = container.TranslatePoint(new Point(0, 0), _list);
            var r = new Rect(top.X + 6, top.Y, Math.Max(0, container.ActualWidth - 12), container.ActualHeight);
            var y = p.Y - top.Y;
            var h = container.ActualHeight;
            if (row.Node.IsFolder && y >= h * 0.25 && y < h * 0.75)
            {
                return (row.Node, -1, r, null);
            }
            if (IsSearching || row.Node.Parent == null)
            {
                return row.Node.IsFolder ? (row.Node, -1, r, null) : null;
            }
            var before = y < h / 2;
            var lineY = before ? top.Y : top.Y + h;
            var index = row.Node.IndexInParent + (before ? 0 : 1);
            return (row.Node.Parent, index, null, (new Point(12, lineY), new Point(w - 12, lineY)));
        }

        if (IsSearching)
        {
            return null;
        }
        // 清單下方空白處：放到最後
        var lastY = 6.0;
        if (_rows.Count > 0 && _list.ItemContainerGenerator.ContainerFromIndex(_rows.Count - 1) is ListBoxItem last && last.IsVisible)
        {
            lastY = last.TranslatePoint(new Point(0, last.ActualHeight), _list).Y;
        }
        return (_folder, -1, null, (new Point(12, lastY), new Point(w - 12, lastY)));
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var nodes = BookmarkDrag.GetNodes(e.Data, _store);
        var page = nodes == null ? BookmarkDrag.GetPage(e.Data) : null;
        var target = nodes != null || page != null ? ListDropTarget(e) : null;
        if (target is not { } t || (nodes != null && nodes.All(n => !BookmarkStore.CanMove(n, t.Parent))))
        {
            e.Effects = DragDropEffects.None;
            _listIndicator?.Clear();
            return;
        }
        e.Effects = nodes != null ? DragDropEffects.Move : (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link : DragDropEffects.Copy;
        _listIndicator ??= DropIndicatorAdorner.Attach(_list);
        if (t.Box is { } box)
        {
            _listIndicator?.ShowBox(box);
        }
        else if (t.Line is { } line)
        {
            _listIndicator?.ShowLine(line.Item1, line.Item2);
        }
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var target = ListDropTarget(e);
        ClearIndicators();
        if (target is { } t)
        {
            BookmarkDrag.Drop(e.Data, _store, t.Parent, t.Index);
        }
    }

    private void Tree_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var item = TreeItemFrom(e.OriginalSource as DependencyObject);
        var folder = item?.Tag as BookmarkNode;
        var nodes = BookmarkDrag.GetNodes(e.Data, _store);
        var ok = folder != null
                 && (nodes != null ? nodes.Any(n => BookmarkStore.CanMove(n, folder)) : BookmarkDrag.GetPage(e.Data) != null);
        if (!ok)
        {
            e.Effects = DragDropEffects.None;
            _treeIndicator?.Clear();
            return;
        }
        e.Effects = nodes != null ? DragDropEffects.Move : DragDropEffects.Copy;
        // 只標示標題那一行（不含子資料夾）
        if (item!.Header is FrameworkElement header)
        {
            var tl = header.TranslatePoint(new Point(0, 0), _tree);
            _treeIndicator ??= DropIndicatorAdorner.Attach(_tree);
            _treeIndicator?.ShowBox(new Rect(tl.X - 4, tl.Y, header.ActualWidth + 8, header.ActualHeight));
        }
    }

    private void Tree_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearIndicators();
        if (TreeItemFrom(e.OriginalSource as DependencyObject)?.Tag is BookmarkNode folder)
        {
            BookmarkDrag.Drop(e.Data, _store, folder, -1);
        }
    }

    private void ClearIndicators()
    {
        _listIndicator?.Detach();
        _listIndicator = null;
        _treeIndicator?.Detach();
        _treeIndicator = null;
    }

    public void FocusList()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_search.IsKeyboardFocusWithin)
            {
                _list.Focus();
            }
        }, DispatcherPriority.Input);
    }
}
