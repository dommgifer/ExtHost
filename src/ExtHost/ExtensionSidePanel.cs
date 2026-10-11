using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ExtHost.Services;
using Microsoft.Web.WebView2.Wpf;

namespace ExtHost;

/// <summary>
/// 擴充功能側邊欄：WebView2 沒有 Chrome 的 Side Panel UI，
/// 由 ExtHost 在主視窗右側放一個 WebView2 載入 manifest 的 side_panel.default_path。
/// 同一時間只顯示一個擴充功能的側邊欄（和 Chrome 一樣），切換分頁時保持開啟。
/// </summary>
public sealed class ExtensionSidePanel : DockPanel
{
    private readonly TextBlock _title;
    private readonly Grid _host;
    private WebView2? _webView;
    private ExtensionItem? _item;
    private ExtensionTabsBridge? _tabsBridge;
    private string? _activeTabUrl;
    private int _generation;

    /// <summary>使用者按了關閉，或頁面呼叫了 window.close()。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>側邊欄裡的連結要開到新分頁。</summary>
    public event EventHandler<string>? OpenUrlRequested;

    public bool IsOpen => _item != null;

    public string? CurrentId => _item?.Id;

    /// <summary>目前側邊欄所屬擴充功能的資料夾（不是由 ExtHost 載入的為 null）。</summary>
    public string? CurrentPath => _item?.Path;

    public ExtensionSidePanel()
    {
        LastChildFill = true;
        Background = Brushes.White;

        var header = new DockPanel
        {
            Height = 36,
            Background = (Brush)Application.Current.FindResource("AddressBrush"),
            LastChildFill = true,
        };
        SetDock(header, Dock.Top);

        header.Children.Add(MakeButton("", "關閉側邊欄", (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty)));
        header.Children.Add(MakeButton("", "檢查側邊欄（DevTools）", (_, _) => _webView?.CoreWebView2?.OpenDevToolsWindow()));
        header.Children.Add(MakeButton("", "重新整理側邊欄", (_, _) => Reload()));

        _title = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 6, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        header.Children.Add(_title);

        Children.Add(header);
        Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.FindResource("BorderBrush"),
        });
        SetDock((UIElement)Children[1], Dock.Top);

        _host = new Grid { Background = Brushes.White };
        Children.Add(_host);
    }

    private static Button MakeButton(string glyph, string tip, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = glyph,
            Style = (Style)Application.Current.FindResource("IconButton"),
            Width = 30,
            Height = 30,
            FontSize = 12,
            ToolTip = tip,
            Margin = new Thickness(0, 0, 2, 0),
        };
        b.Click += onClick;
        SetDock(b, Dock.Right);
        return b;
    }

    /// <summary>ExtHost 目前選取的分頁網址，讓側邊欄的 chrome.tabs.query 找得到它。</summary>
    public void SetActiveTabUrl(string? url)
    {
        _activeTabUrl = url;
        _ = _tabsBridge?.SetActiveTabUrlAsync(url);
    }

    /// <summary>顯示指定擴充功能的側邊欄（會取代目前顯示的）。呼叫前面板必須已經可見。</summary>
    public async Task ShowAsync(ExtensionItem item)
    {
        var url = item.SidePanelUrl ?? throw new InvalidOperationException("此擴充功能沒有側邊欄頁面");
        DisposeWebView();
        _item = item;
        _title.Text = item.Name;
        ToolTip = url;

        var generation = ++_generation;
        var webView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
        _webView = webView;
        _host.Children.Add(webView);

        try
        {
            var env = await BrowserEnvironment.GetAsync();
            await webView.EnsureCoreWebView2Async(env);
            WebView2CloseGuard.DetachDefaultHandler(webView);
        }
        catch when (generation != _generation)
        {
            return; // 初始化期間面板已關閉（WebView2 已釋放）
        }
        if (generation != _generation)
        {
            return; // 初始化期間已經切換到別的擴充功能或已關閉
        }

        var core = webView.CoreWebView2;
        try
        {
            var bridge = await ExtensionTabsBridge.InstallAsync(core, _activeTabUrl);
            if (generation != _generation)
            {
                return;
            }
            _tabsBridge = bridge;
            // 安裝期間可能已切換分頁（當時 _tabsBridge 還是 null，SetActiveTabUrl 只記下網址），補同步最新的
            await bridge.SetActiveTabUrlAsync(_activeTabUrl);
        }
        catch when (generation != _generation)
        {
            return; // 安裝期間面板已關閉（WebView2 已釋放）
        }
        if (generation != _generation)
        {
            return;
        }
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenUrlRequested?.Invoke(this, e.Uri);
        };
        // 延後處理，避免在 WebView2 事件回呼中 Dispose 它
        core.WindowCloseRequested += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (generation == _generation)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        });
        core.Navigate(url);
    }

    /// <summary>擴充功能清單刷新後，換成新的 ExtensionItem 物件（同一個擴充功能）。</summary>
    public void UpdateItem(ExtensionItem item)
    {
        if (_item != null && item.Id == _item.Id)
        {
            _item = item;
            _title.Text = item.Name;
        }
    }

    /// <summary>重新載入側邊欄頁面（擴充功能重新載入後，舊頁面的 context 已失效）。</summary>
    public void Reload()
    {
        var url = _item?.SidePanelUrl;
        var core = _webView?.CoreWebView2;
        if (url != null && core != null)
        {
            try
            {
                core.Navigate(url);
            }
            catch (Exception ex)
            {
                AppPaths.Log("重新整理側邊欄失敗：" + ex.Message);
            }
        }
    }

    public void ClosePanel()
    {
        _generation++;
        _item = null;
        _title.Text = "";
        DisposeWebView();
    }

    private void DisposeWebView()
    {
        if (_webView == null)
        {
            return;
        }
        var old = _webView;
        _webView = null;
        _tabsBridge = null;
        _host.Children.Remove(old);
        try
        {
            old.Dispose();
        }
        catch
        {
        }
    }
}
