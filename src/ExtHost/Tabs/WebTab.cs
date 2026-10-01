using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExtHost.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ExtHost.Tabs;

/// <summary>一個網頁分頁 = 一個 WebView2 控制項。</summary>
public sealed class WebTab : TabBase
{
    private readonly WebView2 _webView;
    private bool _canGoBack;
    private bool _canGoForward;
    private string _statusText = "";
    private bool _closed;

    public WebTab()
    {
        _webView = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.White,
        };
        Title = "新分頁";
    }

    public override FrameworkElement View => _webView;

    public WebView2 WebView => _webView;

    public CoreWebView2? Core => _webView.CoreWebView2;

    public bool CanGoBack
    {
        get => _canGoBack;
        private set => Set(ref _canGoBack, value);
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        private set => Set(ref _canGoForward, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>此分頁是否由網頁腳本開啟（window.open、target=_blank）。</summary>
    public bool OpenedByScript { get; set; }

    /// <summary>
    /// 比照 Chrome 的規則判斷網頁能否用 window.close() 關閉此分頁：
    /// 由腳本開啟的分頁，或歷史紀錄只有一筆的分頁才可以。
    /// WebView2 不做這個檢查，一律觸發 WindowCloseRequested，所以由宿主自己判斷。
    /// </summary>
    public async Task<bool> IsScriptClosableAsync()
    {
        if (OpenedByScript)
        {
            return true;
        }
        var core = Core;
        if (core == null)
        {
            return false;
        }
        try
        {
            var result = await core.ExecuteScriptAsync("history.length");
            return int.TryParse(result, out var length) ? length <= 1 : !core.CanGoBack && !core.CanGoForward;
        }
        catch
        {
            return !core.CanGoBack && !core.CanGoForward;
        }
    }

    /// <summary>在頁面的 DevTools console 印出警告（比照 Chrome 擋下 window.close() 時的訊息）。</summary>
    public void ConsoleWarn(string message)
    {
        try
        {
            _ = Core?.ExecuteScriptAsync("console.warn(" + System.Text.Json.JsonSerializer.Serialize(message) + ")");
        }
        catch
        {
        }
    }

    public event EventHandler<CoreWebView2NewWindowRequestedEventArgs>? NewWindowRequested;

    /// <summary>網頁呼叫了 window.close()。</summary>
    public event EventHandler? CloseRequested;
    public event EventHandler<bool>? FullScreenChanged;

    public async Task InitializeAsync(CoreWebView2Environment env)
    {
        await _webView.EnsureCoreWebView2Async(env);
        var core = _webView.CoreWebView2;

        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsZoomControlEnabled = true;

        core.SourceChanged += (_, _) => Url = core.Source;
        core.DocumentTitleChanged += (_, _) => Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? core.Source : core.DocumentTitle;
        core.HistoryChanged += (_, _) =>
        {
            CanGoBack = core.CanGoBack;
            CanGoForward = core.CanGoForward;
        };
        core.NavigationStarting += (_, _) => IsLoading = true;
        core.NavigationCompleted += (_, _) =>
        {
            IsLoading = false;
            CanGoBack = core.CanGoBack;
            CanGoForward = core.CanGoForward;
        };
        core.FaviconChanged += async (_, _) => await UpdateFaviconAsync();
        core.StatusBarTextChanged += (_, _) => StatusText = core.StatusBarText ?? "";
        core.NewWindowRequested += (_, e) => NewWindowRequested?.Invoke(this, e);
        core.WindowCloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        core.ContainsFullScreenElementChanged += (_, _) => FullScreenChanged?.Invoke(this, core.ContainsFullScreenElement);
        core.ProcessFailed += (_, e) =>
        {
            AppPaths.Log($"WebView2 程序失敗：{e.ProcessFailedKind} {e.Reason}");
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited
                || e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                Title = "此頁面已停止回應（按 F5 重新整理）";
                IsLoading = false;
            }
        };
    }

    public void Navigate(string url)
    {
        if (Core == null)
        {
            return;
        }
        try
        {
            Core.Navigate(url);
        }
        catch (ArgumentException)
        {
            MessageBox.Show("無效的網址：" + url, "ExtHost", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void GoBack()
    {
        if (Core?.CanGoBack == true)
        {
            Core.GoBack();
        }
    }

    public void GoForward()
    {
        if (Core?.CanGoForward == true)
        {
            Core.GoForward();
        }
    }

    public void Reload() => Core?.Reload();

    public void Stop() => Core?.Stop();

    public void OpenDevTools() => Core?.OpenDevToolsWindow();

    public override void OnActivated()
    {
        if (!UrlHelper.IsBlank(Url))
        {
            _webView.Focus();
        }
    }

    public override void Close()
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        try
        {
            _webView.Dispose();
        }
        catch
        {
        }
    }

    private async Task UpdateFaviconAsync()
    {
        var core = Core;
        if (core == null)
        {
            return;
        }
        try
        {
            if (string.IsNullOrEmpty(core.FaviconUri))
            {
                Icon = null;
                return;
            }
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream == null)
            {
                Icon = null;
                return;
            }
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            if (ms.Length == 0)
            {
                Icon = null;
                return;
            }
            FaviconCache.Store(core.Source, ms.ToArray());
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            Icon = bmp;
        }
        catch
        {
            Icon = null;
        }
    }
}
