using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using ExtHost.Bookmarks;
using ExtHost.Services;
using ExtHost.Tabs;
using Microsoft.Web.WebView2.Core;

namespace ExtHost;

public partial class MainWindow : Window, IBrowserShell, IBookmarkHost
{
    private readonly ObservableCollection<TabBase> _tabs = new();
    private CoreWebView2Environment? _env;
    private bool _isFullScreen;
    private WindowState _stateBeforeFullScreen;
    private bool _isClosing;

    public AppSettings Settings => App.Settings;

    public ExtensionManager? Extensions { get; private set; }

    public BookmarkStore Bookmarks { get; }

    public Window OwnerWindow => this;

    private TabBase? SelectedTab => TabList.SelectedItem as TabBase;

    public MainWindow()
    {
        InitializeComponent();
        TabList.ItemsSource = _tabs;

        Bookmarks = BookmarkStore.Load();
        Bookmarks.Changed += (_, _) => UpdateStar();
        BookmarkBarControl.Initialize(this);

        RestorePlacement();
        ApplyDevMode();

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += (_, _) => UpdateMaximizeState();
        SizeChanged += (_, e) =>
        {
            if (e.WidthChanged && SidePanel.IsOpen)
            {
                ApplySidePanelWidth();
            }
        };
        SourceInitialized += (_, _) =>
        {
            if (Settings.WindowMaximized)
            {
                WindowState = WindowState.Maximized;
            }
            UpdateMaximizeState();
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ======================= 啟動 =======================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _env = await BrowserEnvironment.GetAsync();
        }
        catch (Exception ex)
        {
            AppPaths.Log("建立 WebView2 環境失敗：" + ex);
            MessageBox.Show(this, "無法啟動 WebView2：\n" + ex.Message + "\n\n若另一個 ExtHost 正以不同設定執行，請先關閉它。",
                "ExtHost", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        RuntimeText.Text = "WebView2 " + _env.BrowserVersionString;

        if (Bookmarks.LoadError != null)
        {
            // 書籤檔讀不到：已停止寫入，提醒使用者如何復原
            var answer = MessageBox.Show(this,
                "無法讀取書籤檔。為避免覆寫原本的書籤，ExtHost 已停止儲存書籤，這段期間無法新增或修改書籤。\n\n"
                + "原因：" + Bookmarks.LoadError + "\n\n"
                + "復原方式：關閉 ExtHost，修復 Bookmarks.json（或用備份檔取代它；若不需要舊書籤也可以直接刪除），再重新啟動。\n\n"
                + "要開啟資料資料夾嗎？",
                "書籤", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                StartShell("explorer.exe", $"/select,\"{AppPaths.BookmarksFile}\"");
            }
        }
        ProfileText.Text = AppPaths.IsPortable ? "可攜模式" : "";

        WebTab first;
        try
        {
            first = await CreateWebTabAsync(null, select: true);
        }
        catch (Exception ex)
        {
            AppPaths.Log("建立分頁失敗：" + ex);
            MessageBox.Show(this, "無法建立瀏覽器分頁：\n" + ex.Message, "ExtHost", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        // 擴充功能要在第一個頁面載入前準備好，content script 才會注入到第一頁
        if (first.Core != null)
        {
            Extensions = new ExtensionManager(first.Core.Profile, Settings, Dispatcher);
            Extensions.Changed += (_, _) =>
            {
                UpdateExtensionUi();
                SyncSidePanelWithExtensions();
            };
            Extensions.Reloaded += (_, e) =>
            {
                OnExtensionsReloaded();
                // 只有側邊欄所屬的擴充功能被重新載入時，舊頁面的 context 才會失效；
                // 其他擴充功能重新載入時不刷新，避免清掉側邊欄的輸入與捲動位置
                var path = SidePanel.CurrentPath;
                if (SidePanel.IsOpen && path != null && e.Paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    SidePanel.Reload();
                }
            };
            SidePanel.CloseRequested += (_, _) => CloseSidePanel();
            SidePanel.OpenUrlRequested += (_, url) => OpenInNewTab(url);
            Extensions.Error += (_, msg) => ShowError(msg);
            ExtToolbar.ItemsSource = Extensions.ToolbarItems;
            try
            {
                await Extensions.InitializeAsync();
            }
            catch (Exception ex)
            {
                AppPaths.Log("初始化擴充功能失敗：" + ex);
                ShowError("初始化擴充功能失敗：" + ExtensionManager.Describe(ex)
                    + "\n\n可能原因：WebView2 Runtime 版本太舊（需支援擴充功能），或組織政策停用了此功能。");
            }
            UpdateExtensionUi();
        }

        // 還原上次的分頁
        var urls = Settings.RestoreSession
            ? Settings.LastSession.Where(u => !string.IsNullOrWhiteSpace(u)).ToList()
            : new List<string>();
        if (urls.Count == 0)
        {
            urls.Add(Settings.HomePage);
        }

        var firstUsed = false;
        foreach (var url in urls)
        {
            if (url == UrlHelper.ExtensionsPageUrl)
            {
                OpenExtensionsTab(select: false);
            }
            else if (url == UrlHelper.BookmarksPageUrl)
            {
                OpenBookmarkManagerTab(select: false);
            }
            else if (!firstUsed)
            {
                firstUsed = true;
                if (!UrlHelper.IsBlank(url))
                {
                    first.Navigate(url);
                }
            }
            else
            {
                try
                {
                    await CreateWebTabAsync(url, select: false);
                }
                catch (Exception ex)
                {
                    AppPaths.Log("還原分頁失敗：" + ex.Message);
                }
            }
        }

        TabList.SelectedItem = first;
        if (UrlHelper.IsBlank(first.Url))
        {
            FocusAddressBar();
        }
    }

    // ======================= 分頁 =======================

    private async Task<WebTab> CreateWebTabAsync(string? url, bool select, int? index = null)
    {
        if (_env == null)
        {
            throw new InvalidOperationException("WebView2 環境尚未就緒");
        }

        var tab = new WebTab();
        tab.View.Visibility = Visibility.Hidden;
        ContentHost.Children.Add(tab.View);

        if (index is { } i && i >= 0 && i <= _tabs.Count)
        {
            _tabs.Insert(i, tab);
        }
        else
        {
            _tabs.Add(tab);
        }

        tab.PropertyChanged += Tab_PropertyChanged;
        tab.NewWindowRequested += OnNewWindowRequested;
        // 不能在 WebView2 自己的事件回呼裡同步 Dispose 它（重入會導致程式崩潰），延後到事件結束後處理
        tab.CloseRequested += (_, _) => Dispatcher.BeginInvoke(async () => await CloseTabFromPageAsync(tab));
        tab.FullScreenChanged += (_, full) =>
        {
            if (tab == SelectedTab)
            {
                SetFullScreen(full);
            }
        };

        if (select)
        {
            TabList.SelectedItem = tab;
        }

        await tab.InitializeAsync(_env);

        if (url != null && !UrlHelper.IsBlank(url))
        {
            tab.Navigate(url);
        }
        return tab;
    }

    public void OpenInNewTab(string url)
    {
        var index = SelectedTab != null ? _tabs.IndexOf(SelectedTab) + 1 : (int?)null;
        _ = CreateTabSafeAsync(url, index);
    }

    private async Task CreateTabSafeAsync(string? url, int? index)
    {
        try
        {
            var tab = await CreateWebTabAsync(url, select: true, index: index);
            if (UrlHelper.IsBlank(url))
            {
                FocusAddressBar();
            }
            else
            {
                tab.WebView.Focus();
            }
        }
        catch (Exception ex)
        {
            ShowError("無法開啟新分頁：" + ex.Message);
        }
    }

    private void OpenExtensionsTab(bool select = true)
    {
        var existing = _tabs.OfType<ExtensionsTab>().FirstOrDefault();
        if (existing == null)
        {
            existing = new ExtensionsTab(this);
            existing.View.Visibility = Visibility.Hidden;
            ContentHost.Children.Add(existing.View);
            var index = SelectedTab != null ? _tabs.IndexOf(SelectedTab) + 1 : _tabs.Count;
            _tabs.Insert(Math.Min(index, _tabs.Count), existing);
            existing.PropertyChanged += Tab_PropertyChanged;
        }
        if (select)
        {
            TabList.SelectedItem = existing;
        }
    }

    private BookmarksTab OpenBookmarkManagerTab(bool select = true)
    {
        var existing = _tabs.OfType<BookmarksTab>().FirstOrDefault();
        if (existing == null)
        {
            existing = new BookmarksTab(this);
            existing.View.Visibility = Visibility.Hidden;
            ContentHost.Children.Add(existing.View);
            var index = SelectedTab != null ? _tabs.IndexOf(SelectedTab) + 1 : _tabs.Count;
            _tabs.Insert(Math.Min(index, _tabs.Count), existing);
            existing.PropertyChanged += Tab_PropertyChanged;
        }
        if (select)
        {
            TabList.SelectedItem = existing;
        }
        return existing;
    }

    private void CloseTab(TabBase tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        var wasSelected = SelectedTab == tab;
        tab.PropertyChanged -= Tab_PropertyChanged;
        _tabs.RemoveAt(index);
        ContentHost.Children.Remove(tab.View);
        tab.Close();

        if (_tabs.Count == 0)
        {
            if (!_isClosing)
            {
                Close();
            }
            return;
        }

        if (wasSelected)
        {
            TabList.SelectedItem = _tabs[Math.Min(index, _tabs.Count - 1)];
        }
    }

    /// <summary>
    /// 網頁執行 window.close() 時的處理：
    /// 1. 比照 Chrome，只有腳本開啟或歷史紀錄只有一筆的分頁才會被關閉，其餘忽略並在 console 警告
    /// 2. 只關閉該分頁，絕不因此關閉整個程式；若是最後一個分頁，先補開一個空白分頁
    /// </summary>
    private async Task CloseTabFromPageAsync(WebTab tab)
    {
        if (_isClosing || !_tabs.Contains(tab))
        {
            return;
        }

        try
        {
            if (!await tab.IsScriptClosableAsync())
            {
                tab.ConsoleWarn("Scripts may close only the windows that were opened by them. (ExtHost 已忽略 window.close())");
                return;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("判斷 window.close() 是否允許時失敗：" + ex.Message);
            return;
        }

        if (!_tabs.Contains(tab))
        {
            return;
        }

        if (_tabs.Count == 1)
        {
            try
            {
                await CreateWebTabAsync(Settings.HomePage, select: true);
            }
            catch (Exception ex)
            {
                AppPaths.Log("補開分頁失敗，保留原分頁：" + ex.Message);
                return;
            }
        }

        CloseTab(tab);
    }

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var index = sender is TabBase src ? _tabs.IndexOf(src) + 1 : (int?)null;
            var tab = await CreateWebTabAsync(null, select: true, index: index);
            tab.OpenedByScript = true; // 允許此分頁用 window.close() 關閉自己（同 Chrome）
            if (tab.Core != null)
            {
                e.NewWindow = tab.Core;
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("處理新視窗失敗：" + ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SelectedTab;
        foreach (var t in _tabs)
        {
            t.View.Visibility = t == selected ? Visibility.Visible : Visibility.Hidden;
        }

        if (_isFullScreen)
        {
            SetFullScreen(false);
        }

        UpdateToolbar();
        HoverText.Text = "";
        SyncActiveTabUrlToExtensions();

        if (selected != null)
        {
            if (selected is WebTab w && UrlHelper.IsBlank(w.Url))
            {
                FocusAddressBar();
            }
            else
            {
                selected.OnActivated();
            }
        }
    }

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender != SelectedTab)
        {
            return;
        }
        if (e.PropertyName == nameof(WebTab.StatusText) && sender is WebTab w)
        {
            HoverText.Text = w.StatusText;
            return;
        }
        if (e.PropertyName == nameof(TabBase.Url))
        {
            SyncActiveTabUrlToExtensions();
        }
        UpdateToolbar();
    }

    private void TabItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && (sender as FrameworkElement)?.DataContext is TabBase tab)
        {
            CloseTab(tab);
            e.Handled = true;
        }
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TabBase tab)
        {
            CloseTab(tab);
        }
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => _ = CreateTabSafeAsync(Settings.HomePage, null);

    private void SelectTabByOffset(int offset)
    {
        if (_tabs.Count == 0)
        {
            return;
        }
        var i = SelectedTab != null ? _tabs.IndexOf(SelectedTab) : 0;
        i = ((i + offset) % _tabs.Count + _tabs.Count) % _tabs.Count;
        TabList.SelectedItem = _tabs[i];
    }

    // ======================= 工具列 =======================

    private void UpdateToolbar()
    {
        var tab = SelectedTab;
        if (tab == null)
        {
            return;
        }

        Title = string.IsNullOrWhiteSpace(tab.Title) ? "ExtHost" : tab.Title + " - ExtHost";

        if (tab is WebTab w)
        {
            BackButton.IsEnabled = w.CanGoBack;
            ForwardButton.IsEnabled = w.CanGoForward;
            ReloadButton.IsEnabled = true;
            ReloadButton.Content = w.IsLoading ? "" : "";
            ReloadButton.ToolTip = w.IsLoading ? "停止載入" : "重新整理 (F5)";
        }
        else
        {
            BackButton.IsEnabled = false;
            ForwardButton.IsEnabled = false;
            ReloadButton.IsEnabled = true;
            ReloadButton.Content = "";
        }

        var url = tab.Url;
        if (!AddressBox.IsKeyboardFocusWithin)
        {
            AddressBox.Text = UrlHelper.IsBlank(url) ? "" : url;
        }

        SecurityIcon.Text = UrlHelper.IsSecure(url) ? ""
            : UrlHelper.IsExtensionUrl(url) || tab is ExtensionsTab or BookmarksTab ? ""
            : UrlHelper.IsBlank(url) ? ""
            : "";
        SecurityIcon.ToolTip = UrlHelper.IsSecure(url) ? "安全連線 (HTTPS)"
            : UrlHelper.IsBlank(url) ? null
            : tab is BookmarksTab ? "書籤管理員"
            : tab is ExtensionsTab || UrlHelper.IsExtensionUrl(url) ? "擴充功能頁面"
            : "非加密連線";

        UpdateInjectChip();
        UpdateStar();
        UpdateBookmarkBarVisibility();
    }

    private void UpdateInjectChip()
    {
        var url = SelectedTab?.Url;
        var matches = Extensions?.MatchingContentScripts(url) ?? new List<ExtensionItem>();
        if (matches.Count == 0 || SelectedTab is not WebTab)
        {
            InjectChip.Visibility = Visibility.Collapsed;
            return;
        }
        var names = matches.Select(m => m.Name).ToList();
        InjectChipText.Text = names.Count <= 2
            ? "注入：" + string.Join("、", names)
            : $"注入：{names[0]} 等 {names.Count} 個";
        InjectChip.ToolTip = "此頁面符合下列擴充功能的 content_scripts 規則：\n" + string.Join("\n", names);
        InjectChip.Visibility = Visibility.Visible;
    }

    private void UpdateExtensionUi()
    {
        if (Extensions == null)
        {
            ExtCountText.Text = "";
            return;
        }
        ExtCountText.Text = $"擴充功能：{Extensions.EnabledCount} 啟用 / {Extensions.DisabledCount} 停用";
        UpdateInjectChip();
    }

    private void OnExtensionsReloaded()
    {
        if (Settings.DevMode && Settings.ReloadTabAfterExtensionReload && SelectedTab is WebTab w && !UrlHelper.IsBlank(w.Url))
        {
            w.Reload();
        }
    }

    public void ApplyDevMode()
    {
        var v = Settings.DevMode ? Visibility.Visible : Visibility.Collapsed;
        DevBadge.Visibility = v;
        DevReloadButton.Visibility = v;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => (SelectedTab as WebTab)?.GoBack();

    private void Forward_Click(object sender, RoutedEventArgs e) => (SelectedTab as WebTab)?.GoForward();

    private void Reload_Click(object sender, RoutedEventArgs e) => ReloadCurrent();

    private void ReloadCurrent()
    {
        switch (SelectedTab)
        {
            case WebTab w when w.IsLoading:
                w.Stop();
                break;
            case WebTab w:
                w.Reload();
                break;
            case ExtensionsTab x:
                _ = Extensions?.RefreshAsync();
                x.OnActivated();
                break;
            case BookmarksTab b:
                b.OnActivated();
                break;
        }
    }

    private void DevTools_Click(object sender, RoutedEventArgs e) => (SelectedTab as WebTab)?.OpenDevTools();

    private async void ReloadExtensions_Click(object sender, RoutedEventArgs e)
    {
        if (Extensions != null)
        {
            await Extensions.ReloadAllAsync();
        }
    }

    // ======================= 網址列 =======================

    private void FocusAddressBar()
    {
        Dispatcher.BeginInvoke(() =>
        {
            AddressBox.Focus();
            Keyboard.Focus(AddressBox);
            AddressBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void AddressBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!AddressBox.IsKeyboardFocusWithin)
        {
            AddressBox.Focus();
            e.Handled = true;
        }
    }

    private void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        AddressBox.SelectAll();
        AddressBorder.BorderBrush = (Brush)FindResource("AccentBrush");
        AddressBorder.Background = Brushes.White;
    }

    private void AddressBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        AddressBorder.BorderBrush = Brushes.Transparent;
        AddressBorder.Background = (Brush)FindResource("AddressBrush");
        UpdateToolbar();
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateFromAddressBar(AddressBox.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            var url = SelectedTab?.Url;
            AddressBox.Text = UrlHelper.IsBlank(url) ? "" : url;
            AddressBox.SelectAll();
            e.Handled = true;
        }
    }

    private void NavigateFromAddressBar(string text)
    {
        if (text.Trim().Equals(UrlHelper.ExtensionsPageUrl, StringComparison.OrdinalIgnoreCase))
        {
            OpenExtensionsTab();
            return;
        }
        if (text.Trim().TrimEnd('/') is var t
            && (t.Equals(UrlHelper.BookmarksPageUrl, StringComparison.OrdinalIgnoreCase)
                || t.Equals("chrome://bookmarks", StringComparison.OrdinalIgnoreCase)))
        {
            OpenBookmarkManager();
            return;
        }

        var url = UrlHelper.ToNavigableUrl(text, Settings.SearchUrl);
        if (url == null)
        {
            return;
        }

        if (SelectedTab is WebTab w)
        {
            w.Navigate(url);
            w.WebView.Focus();
        }
        else
        {
            _ = CreateTabSafeAsync(url, null);
        }
    }

    // ======================= 擴充功能 UI =======================

    public void ShowExtensionPopup(ExtensionItem item, FrameworkElement anchor)
    {
        if (item.HasPopup)
        {
            _ = ExtensionPopupWindow.ShowForAsync(item, anchor, this, ActiveWebUrl);
        }
    }

    private void ExtButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is ExtensionItem item)
        {
            if (item.HasPopup)
            {
                ShowExtensionPopup(item, b);
            }
            else if (item.HasSidePanel)
            {
                _ = ToggleSidePanelAsync(item);
            }
            else
            {
                ShowExtensionContextMenu(item, b);
            }
        }
    }

    // ----- 擴充功能側邊欄 -----

    /// <summary>目前分頁的網址（給擴充功能的 chrome.tabs.query 使用）；不是網頁分頁時為 null。</summary>
    private string? ActiveWebUrl => SelectedTab is WebTab w && !UrlHelper.IsBlank(w.Url) ? w.Url : null;

    /// <summary>把目前分頁網址同步給側邊欄與開著的 popup（含釘選中的）。</summary>
    private void SyncActiveTabUrlToExtensions()
    {
        var url = ActiveWebUrl;
        SidePanel.SetActiveTabUrl(url);
        ExtensionPopupWindow.SetActiveTabUrl(url);
    }

    private async Task ToggleSidePanelAsync(ExtensionItem item)
    {
        if (SidePanel.IsOpen && SidePanel.CurrentId == item.Id)
        {
            CloseSidePanel();
            return;
        }

        SidePanelSplitter.Visibility = Visibility.Visible;
        SidePanel.Visibility = Visibility.Visible;
        ApplySidePanelWidth(forceOpen: true);
        SidePanel.SetActiveTabUrl(ActiveWebUrl);

        try
        {
            await SidePanel.ShowAsync(item);
        }
        catch (Exception ex)
        {
            AppPaths.Log("開啟側邊欄失敗：" + ex);
            CloseSidePanel();
            ShowError("無法開啟側邊欄：" + ExtensionManager.Describe(ex));
        }
    }

    private void CloseSidePanel()
    {
        SidePanel.ClosePanel();
        SidePanel.Visibility = Visibility.Collapsed;
        SidePanelSplitter.Visibility = Visibility.Collapsed;
        SidePanelSplitterCol.Width = new GridLength(0);
        SidePanelCol.Width = new GridLength(0);
    }

    /// <summary>擴充功能被停用、移除或重新整理清單時，同步側邊欄狀態。</summary>
    private void SyncSidePanelWithExtensions()
    {
        if (!SidePanel.IsOpen || Extensions == null)
        {
            return;
        }
        var current = Extensions.Items.FirstOrDefault(i => i.Id == SidePanel.CurrentId && i.HasSidePanel);
        if (current == null)
        {
            CloseSidePanel();
        }
        else
        {
            SidePanel.UpdateItem(current);
        }
    }

    private const double SidePanelSplitterWidth = 4;

    /// <summary>
    /// 依儲存的寬度設定側邊欄欄寬，並限制在視窗可見寬度內
    /// （扣掉外框、內容欄 MinWidth 與分隔線），避免側邊欄右側的按鈕被擠出視窗。
    /// 不能用內容區的 ActualWidth 計算：欄寬總和超過可用空間時，WPF 會以未裁切的
    /// DesiredSize 排版再裁切顯示，內容區與上層容器的 ActualWidth 都會被撐大；只有視窗本身的寬度受螢幕限制。
    /// </summary>
    private void ApplySidePanelWidth(bool forceOpen = false)
    {
        if ((!SidePanel.IsOpen && !forceOpen) || _isFullScreen)
        {
            SidePanelSplitterCol.Width = new GridLength(0);
            SidePanelCol.Width = new GridLength(0);
            return;
        }
        var width = Math.Clamp(Settings.SidePanelWidth, 260, 900);
        if (ActualWidth > 0)
        {
            var visible = ActualWidth
                - RootBorder.Margin.Left - RootBorder.Margin.Right
                - RootBorder.BorderThickness.Left - RootBorder.BorderThickness.Right;
            var available = visible - ContentCol.MinWidth - SidePanelSplitterWidth;
            width = Math.Min(width, Math.Max(0, available));
        }
        SidePanelSplitterCol.Width = new GridLength(SidePanelSplitterWidth);
        SidePanelCol.Width = new GridLength(width);
    }

    private void SidePanelSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (SidePanelCol.ActualWidth > 0)
        {
            Settings.SidePanelWidth = Math.Round(SidePanelCol.ActualWidth);
            Settings.Save();
        }
    }

    private void ExtButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button b && b.Tag is ExtensionItem item)
        {
            ShowExtensionContextMenu(item, b);
            e.Handled = true;
        }
    }

    private void ShowExtensionContextMenu(ExtensionItem item, FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = $"{item.Name} {item.Version}", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        menu.Items.Add(new Separator());
        if (item.HasPopup)
        {
            menu.Items.Add(MakeMenuItem("開啟 popup", () => ShowExtensionPopup(item, anchor)));
        }
        if (item.HasSidePanel)
        {
            var isOpen = SidePanel.IsOpen && SidePanel.CurrentId == item.Id;
            menu.Items.Add(MakeMenuItem(isOpen ? "關閉側邊欄" : "開啟側邊欄", () => _ = ToggleSidePanelAsync(item)));
        }
        if (item.HasOptions)
        {
            menu.Items.Add(MakeMenuItem("選項", () => OpenInNewTab(item.OptionsUrl!)));
        }
        if (item.HasPath)
        {
            menu.Items.Add(MakeMenuItem("重新載入", async () => { if (Extensions != null) { await Extensions.ReloadAsync(item); } }));
        }
        menu.Items.Add(MakeMenuItem("停用", async () => { if (Extensions != null) { await Extensions.SetEnabledAsync(item, false); } }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("管理擴充功能", () => OpenExtensionsTab()));
        menu.IsOpen = true;
    }

    private void ExtMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        if (Extensions == null || Extensions.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "尚未載入擴充功能", IsEnabled = false });
        }
        else
        {
            foreach (var item in Extensions.Items)
            {
                var captured = item;
                var mi = new MenuItem
                {
                    Header = item.Name + (item.IsInstalled ? "" : "（未載入）"),
                    IsCheckable = true,
                    IsChecked = item.IsInstalled && item.IsEnabled,
                    ToolTip = item.IsEnabled ? "點擊停用" : "點擊啟用",
                    StaysOpenOnClick = false,
                };
                mi.Click += async (_, _) => await Extensions.SetEnabledAsync(captured, !(captured.IsInstalled && captured.IsEnabled));
                menu.Items.Add(mi);
            }
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("載入未封裝的擴充功能…", async () => await LoadUnpackedWithDialogAsync()));
        if (Settings.DevMode)
        {
            menu.Items.Add(MakeMenuItem("全部重新載入", async () => { if (Extensions != null) { await Extensions.ReloadAllAsync(); } }, "Ctrl+Shift+R"));
        }
        menu.Items.Add(MakeMenuItem("管理擴充功能", () => OpenExtensionsTab(), "Ctrl+Shift+E"));
        menu.IsOpen = true;
    }

    public async Task LoadUnpackedWithDialogAsync()
    {
        if (Extensions == null)
        {
            ShowError("擴充功能系統尚未就緒。");
            return;
        }
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "選擇擴充功能資料夾（含 manifest.json）",
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            await Extensions.LoadUnpackedAsync(dlg.FolderName);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    // ======================= 書籤 =======================

    public bool IsBookmarkBarPinned => Settings.ShowBookmarkBar;

    private bool CanBookmarkCurrent => SelectedTab is WebTab w && !UrlHelper.IsBlank(w.Url);

    private void UpdateStar()
    {
        if (!CanBookmarkCurrent)
        {
            StarButton.Visibility = Visibility.Collapsed;
            return;
        }
        var bookmarked = Bookmarks.FindByUrl(SelectedTab!.Url) != null;
        StarButton.Visibility = Visibility.Visible;
        StarButton.Content = bookmarked ? BookmarkUi.StarFillGlyph : BookmarkUi.StarGlyph;
        StarButton.Foreground = (Brush)FindResource(bookmarked ? "AccentBrush" : "MutedBrush");
        StarButton.ToolTip = bookmarked ? "編輯此分頁的書籤 (Ctrl+D)" : "將這個分頁加入書籤 (Ctrl+D)";
    }

    /// <summary>與 Chrome 相同：沒有開啟「顯示書籤列」時，只在新分頁顯示書籤列。</summary>
    private void UpdateBookmarkBarVisibility()
    {
        var onNewTab = SelectedTab is WebTab w && UrlHelper.IsBlank(w.Url);
        var show = !_isFullScreen && (Settings.ShowBookmarkBar || onNewTab);
        BookmarkBarControl.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // 書籤列和工具列之間不畫分隔線
        ToolbarBorder.BorderThickness = new Thickness(0, 0, 0, show ? 0 : 1);
    }

    public void ToggleBookmarkBar()
    {
        Settings.ShowBookmarkBar = !Settings.ShowBookmarkBar;
        Settings.Save();
        UpdateBookmarkBarVisibility();
    }

    private void Star_Click(object sender, RoutedEventArgs e) => ShowBookmarkBubble();

    private void ShowBookmarkBubble()
    {
        if (SelectedTab is not WebTab w || UrlHelper.IsBlank(w.Url))
        {
            return;
        }
        var folder = Settings.LastBookmarkFolderId is { } id && Bookmarks.FindById(id) is { IsFolder: true } f
            ? f
            : Bookmarks.BookmarkBar;
        var title = string.IsNullOrWhiteSpace(w.Title) ? w.Url : w.Title;
        BookmarkBubble.Toggle(this, StarButton, w.Url, title, folder, chosen =>
        {
            if (Settings.LastBookmarkFolderId != chosen.Id)
            {
                Settings.LastBookmarkFolderId = chosen.Id;
                Settings.Save();
            }
        });
    }

    public void ShowImportBookmarksDialog()
    {
        BookmarkBubble.Current?.Close();
        new ImportBookmarksWindow(this).ShowDialog();
    }

    public void OpenUrl(string url, OpenDisposition disposition)
    {
        switch (disposition)
        {
            case OpenDisposition.CurrentTab when SelectedTab is WebTab w:
                w.Navigate(url);
                w.WebView.Focus();
                break;
            case OpenDisposition.CurrentTab:
            case OpenDisposition.NewForegroundTab:
                OpenInNewTab(url);
                break;
            case OpenDisposition.NewBackgroundTab:
                var index = SelectedTab != null ? _tabs.IndexOf(SelectedTab) + 1 : (int?)null;
                _ = CreateBackgroundTabSafeAsync(url, index);
                break;
        }
    }

    public void OpenUrls(IReadOnlyList<string> urls)
    {
        var start = SelectedTab != null ? _tabs.IndexOf(SelectedTab) + 1 : _tabs.Count;
        for (var i = 0; i < urls.Count; i++)
        {
            if (i == 0)
            {
                _ = CreateTabSafeAsync(urls[i], start);
            }
            else
            {
                _ = CreateBackgroundTabSafeAsync(urls[i], start + i);
            }
        }
    }

    private async Task CreateBackgroundTabSafeAsync(string url, int? index)
    {
        try
        {
            await CreateWebTabAsync(url, select: false, index: index);
        }
        catch (Exception ex)
        {
            ShowError("無法開啟新分頁：" + ex.Message);
        }
    }

    public void OpenBookmarkManager(BookmarkNode? folder = null)
    {
        BookmarkBubble.Current?.Close();
        var tab = OpenBookmarkManagerTab();
        if (folder != null)
        {
            tab.Page.ShowFolder(folder);
        }
    }

    /// <summary>Ctrl+Shift+D：把所有網頁分頁存成一個書籤資料夾。</summary>
    private void BookmarkAllTabs()
    {
        var pages = _tabs.OfType<WebTab>().Where(t => !UrlHelper.IsBlank(t.Url)).ToList();
        if (pages.Count == 0)
        {
            return;
        }
        var folder = Settings.LastBookmarkFolderId is { } id && Bookmarks.FindById(id) is { IsFolder: true } f
            ? f
            : Bookmarks.BookmarkBar;
        if (BookmarkEditorWindow.PromptNewFolderWithTree(this, "將所有分頁加入書籤", folder) is not { } result)
        {
            return;
        }
        var created = BookmarkNode.NewFolder(result.Name);
        foreach (var t in pages)
        {
            var n = BookmarkNode.NewUrl(string.IsNullOrWhiteSpace(t.Title) ? t.Url : t.Title, t.Url);
            n.Parent = created;
            created.Children.Add(n);
        }
        Bookmarks.AddTree(result.Parent, new[] { created });
        Bookmarks.Commit();
    }

    // ---- 拖曳分頁或網址列的網站圖示到書籤列 ----

    private Point _pageDragStart;
    private WebTab? _pageDragTab;

    private void TabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pageDragStart = e.GetPosition(this);
        _pageDragTab = (sender as FrameworkElement)?.DataContext as WebTab;
    }

    private void SecurityIcon_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pageDragStart = e.GetPosition(this);
        _pageDragTab = SelectedTab as WebTab;
    }

    private void PageDragSource_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pageDragTab is not { } tab
            || !BookmarkDrag.IsDragGesture(_pageDragStart, e.GetPosition(this)))
        {
            return;
        }
        _pageDragTab = null;
        if (UrlHelper.IsBlank(tab.Url))
        {
            return;
        }
        var title = string.IsNullOrWhiteSpace(tab.Title) ? tab.Url : tab.Title;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, BookmarkDrag.ForPage(tab.Url, title), DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch (Exception ex)
        {
            AppPaths.Log("拖曳分頁失敗：" + ex.Message);
        }
    }

    private MenuItem BuildBookmarksMenu()
    {
        var root = new MenuItem { Header = "書籤" };
        var add = MakeMenuItem("為這個分頁加入書籤…", ShowBookmarkBubble, "Ctrl+D");
        add.IsEnabled = CanBookmarkCurrent;
        root.Items.Add(add);
        var show = MakeMenuItem("顯示書籤列", ToggleBookmarkBar, "Ctrl+Shift+B");
        show.IsCheckable = true;
        show.IsChecked = Settings.ShowBookmarkBar;
        root.Items.Add(MakeMenuItem("將所有分頁加入書籤…", BookmarkAllTabs, "Ctrl+Shift+D"));
        root.Items.Add(show);
        root.Items.Add(MakeMenuItem("書籤管理員", () => OpenBookmarkManager(), "Ctrl+Shift+O"));
        root.Items.Add(new Separator());
        root.Items.Add(MakeMenuItem("匯入書籤和設定…", ShowImportBookmarksDialog));
        root.Items.Add(MakeMenuItem("匯出書籤…", () => BookmarkUi.ExportWithDialog(this)));
        return root;
    }

    // ======================= 主選單 =======================

    private void MainMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        menu.Items.Add(MakeMenuItem("新增分頁", () => _ = CreateTabSafeAsync(Settings.HomePage, null), "Ctrl+T"));
        menu.Items.Add(MakeMenuItem("擴充功能管理", () => OpenExtensionsTab(), "Ctrl+Shift+E"));
        menu.Items.Add(BuildBookmarksMenu());
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeToggleItem("開發人員模式", Settings.DevMode, v =>
        {
            Settings.DevMode = v;
            ApplyDevMode();
            (_tabs.OfType<ExtensionsTab>().FirstOrDefault())?.OnActivated();
        }));
        menu.Items.Add(MakeToggleItem("監看擴充功能資料夾", Settings.WatchExtensionFolders, v =>
        {
            Settings.WatchExtensionFolders = v;
            Extensions?.UpdateWatchers();
        }));
        menu.Items.Add(MakeToggleItem("重載擴充功能後重新整理分頁", Settings.ReloadTabAfterExtensionReload, v => Settings.ReloadTabAfterExtensionReload = v));
        menu.Items.Add(MakeToggleItem("啟動時還原分頁", Settings.RestoreSession, v => Settings.RestoreSession = v));
        menu.Items.Add(MakeMenuItem("將目前頁面設為首頁", () =>
        {
            if (SelectedTab is WebTab w && !string.IsNullOrEmpty(w.Url))
            {
                Settings.HomePage = w.Url;
                Settings.Save();
            }
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("開啟資料資料夾", () => StartShell("explorer.exe", $"\"{AppPaths.Root}\"")));
        menu.Items.Add(MakeMenuItem("編輯 settings.json", () =>
        {
            Settings.Save();
            StartShell("notepad.exe", $"\"{AppPaths.SettingsFile}\"");
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("關於 ExtHost", ShowAbout));
        menu.IsOpen = true;
    }

    private void ShowAbout()
    {
        // 版本含編譯編號（例如 0.1.0-build.12），去掉 SDK 自動附加的「+commit」
        var info = typeof(App).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        var ver = info?.Split('+')[0] ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        MessageBox.Show(this,
            $"ExtHost {ver}\n\n以 WebView2 為核心的瀏覽器，可載入自製擴充功能。\n\n" +
            $"WebView2 Runtime：{_env?.BrowserVersionString}\n資料位置：{AppPaths.Root}",
            "關於 ExtHost", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private MenuItem MakeMenuItem(string header, Action onClick, string? gesture = null)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private MenuItem MakeMenuItem(string header, Func<Task> onClick, string? gesture = null)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        mi.Click += async (_, _) =>
        {
            try
            {
                await onClick();
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        };
        return mi;
    }

    private MenuItem MakeToggleItem(string header, bool isChecked, Action<bool> onChange)
    {
        var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        mi.Click += (_, _) =>
        {
            onChange(mi.IsChecked);
            Settings.Save();
        };
        return mi;
    }

    private static void StartShell(string file, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    public void ShowError(string message)
    {
        MessageBox.Show(this, message, "ExtHost", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ======================= 鍵盤快捷鍵 =======================

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var shift = mods.HasFlag(ModifierKeys.Shift);
        var alt = mods.HasFlag(ModifierKeys.Alt);
        var handled = true;

        if (ctrl && !shift && !alt && key == Key.T)
        {
            _ = CreateTabSafeAsync(Settings.HomePage, null);
        }
        else if (ctrl && !alt && (key == Key.W || key == Key.F4))
        {
            if (SelectedTab != null)
            {
                CloseTab(SelectedTab);
            }
        }
        else if (ctrl && key == Key.Tab)
        {
            SelectTabByOffset(shift ? -1 : 1);
        }
        else if (ctrl && !shift && key == Key.PageDown)
        {
            SelectTabByOffset(1);
        }
        else if (ctrl && !shift && key == Key.PageUp)
        {
            SelectTabByOffset(-1);
        }
        else if ((ctrl && !shift && key == Key.L) || (alt && !ctrl && key == Key.D) || (mods == ModifierKeys.None && key == Key.F6))
        {
            FocusAddressBar();
        }
        else if ((mods == ModifierKeys.None && key == Key.F5) || (ctrl && !shift && key == Key.R))
        {
            if (SelectedTab is WebTab w)
            {
                w.Reload();
            }
            else
            {
                ReloadCurrent();
            }
        }
        else if (ctrl && shift && key == Key.R)
        {
            if (Extensions != null)
            {
                _ = Extensions.ReloadAllAsync();
            }
        }
        else if (ctrl && shift && key == Key.E)
        {
            OpenExtensionsTab();
        }
        else if (ctrl && !shift && !alt && key == Key.D)
        {
            ShowBookmarkBubble();
        }
        else if (ctrl && shift && !alt && key == Key.B)
        {
            ToggleBookmarkBar();
        }
        else if (ctrl && shift && !alt && key == Key.O)
        {
            OpenBookmarkManager();
        }
        else if (ctrl && shift && !alt && key == Key.D)
        {
            BookmarkAllTabs();
        }
        else if ((mods == ModifierKeys.None && key == Key.F12) || (ctrl && shift && key == Key.I))
        {
            (SelectedTab as WebTab)?.OpenDevTools();
        }
        else if (alt && !ctrl && key == Key.Left)
        {
            (SelectedTab as WebTab)?.GoBack();
        }
        else if (alt && !ctrl && key == Key.Right)
        {
            (SelectedTab as WebTab)?.GoForward();
        }
        else if (alt && !ctrl && key == Key.Home)
        {
            if (SelectedTab is WebTab w)
            {
                w.Navigate(UrlHelper.IsBlank(Settings.HomePage) ? "about:blank" : Settings.HomePage);
            }
        }
        else if (ctrl && !shift && !alt && key >= Key.D1 && key <= Key.D9)
        {
            if (_tabs.Count > 0)
            {
                var n = key - Key.D1;
                TabList.SelectedItem = key == Key.D9 ? _tabs[^1] : _tabs[Math.Min(n, _tabs.Count - 1)];
            }
        }
        else if (_isFullScreen && mods == ModifierKeys.None && key == Key.F11)
        {
            SetFullScreen(false);
        }
        else
        {
            handled = false;
        }

        if (handled)
        {
            e.Handled = true;
        }
    }

    // ======================= 視窗 =======================

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    private void UpdateMaximizeState()
    {
        if (WindowState == WindowState.Maximized)
        {
            // WindowChrome 最大化時視窗邊框會超出螢幕，需補回邊距
            double frame = 8;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                var d = (uint)Math.Round(dpi.PixelsPerInchX);
                var px = GetSystemMetricsForDpi(32 /* SM_CXSIZEFRAME */, d) + GetSystemMetricsForDpi(92 /* SM_CXPADDEDBORDER */, d);
                if (px > 0)
                {
                    frame = px / dpi.DpiScaleX;
                }
            }
            catch
            {
            }
            RootBorder.Margin = new Thickness(frame);
            RootBorder.BorderThickness = new Thickness(0);
            MaxButton.Content = "";
            MaxButton.ToolTip = "還原";
        }
        else
        {
            RootBorder.Margin = new Thickness(0);
            RootBorder.BorderThickness = new Thickness(1);
            MaxButton.Content = "";
            MaxButton.ToolTip = "最大化";
        }
        if (SidePanel.IsOpen)
        {
            ApplySidePanelWidth(); // 外框邊距改變，可見寬度跟著變
        }
    }

    private void SetFullScreen(bool full)
    {
        if (full == _isFullScreen)
        {
            return;
        }
        _isFullScreen = full;
        if (full)
        {
            _stateBeforeFullScreen = WindowState;
            ApplySidePanelWidth();
            TabStripRowDef.Height = new GridLength(0);
            ToolbarRowDef.Height = new GridLength(0);
            StatusRowDef.Height = new GridLength(0);
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
            });
            WindowState = WindowState.Maximized;
        }
        else
        {
            TabStripRowDef.Height = new GridLength(44);
            ToolbarRowDef.Height = new GridLength(48);
            StatusRowDef.Height = new GridLength(26);
            ApplySidePanelWidth();
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 44,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
            WindowState = _stateBeforeFullScreen;
        }
        UpdateBookmarkBarVisibility();
        UpdateMaximizeState();
    }

    private void RestorePlacement()
    {
        Width = Math.Max(MinWidth, Settings.WindowWidth);
        Height = Math.Max(MinHeight, Settings.WindowHeight);
        if (Settings.WindowLeft is { } left && Settings.WindowTop is { } top)
        {
            var vl = SystemParameters.VirtualScreenLeft;
            var vt = SystemParameters.VirtualScreenTop;
            var vw = SystemParameters.VirtualScreenWidth;
            var vh = SystemParameters.VirtualScreenHeight;
            if (left >= vl - 50 && top >= vt - 10 && left + 100 <= vl + vw && top + 50 <= vt + vh)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
                return;
            }
        }
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _isClosing = true;

        // 儲存分頁
        Settings.LastSession = _tabs
            .Select(t => t is ExtensionsTab ? UrlHelper.ExtensionsPageUrl : t.Url)
            .Where(u => !UrlHelper.IsBlank(u))
            .ToList();

        // 儲存視窗位置
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty && !_isFullScreen)
        {
            Settings.WindowLeft = bounds.Left;
            Settings.WindowTop = bounds.Top;
            Settings.WindowWidth = bounds.Width;
            Settings.WindowHeight = bounds.Height;
        }
        Settings.WindowMaximized = WindowState == WindowState.Maximized && !_isFullScreen;
        Settings.Save();

        ExtensionPopupWindow.Current?.Close();
        SidePanel.ClosePanel();
        Extensions?.Shutdown();

        foreach (var t in _tabs.ToList())
        {
            t.Close();
        }
    }
}
