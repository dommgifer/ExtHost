using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ExtHost.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ExtHost;

/// <summary>
/// 擴充功能 popup：無邊框小視窗，內含 WebView2 載入 chrome-extension://&lt;id&gt;/popup.html。
/// 失去焦點會自動關閉（可釘選）；大小依頁面內容自動調整。
/// </summary>
public sealed class ExtensionPopupWindow : Window
{
    private const double MinW = 220;
    private const double MinH = 80;
    private const double MaxW = 800;
    private const double MaxH = 600;
    private const double HeaderH = 28;

    private readonly ExtensionItem _item;
    private readonly WebView2 _webView;
    private readonly ToggleButton _pin;
    private double _anchorRight;
    private double _anchorTop;
    private bool _closing;

    public static ExtensionPopupWindow? Current { get; private set; }

    private static string? _lastClosedId;
    private static DateTime _lastClosedAt;

    public ExtensionPopupWindow(ExtensionItem item)
    {
        _item = item;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = false;
        Width = MinW;
        Height = MinH + HeaderH;
        Background = Brushes.White;
        BorderBrush = (Brush)Application.Current.FindResource("BorderStrongBrush");
        BorderThickness = new Thickness(1);
        Title = item.Name;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");

        var root = new DockPanel();

        // 頂端資訊列：網址、釘選、DevTools、關閉
        var header = new DockPanel
        {
            Height = HeaderH,
            Background = (Brush)Application.Current.FindResource("AddressBrush"),
            LastChildFill = true,
        };
        DockPanel.SetDock(header, Dock.Top);

        var iconFont = (FontFamily)Application.Current.FindResource("IconFont");
        Button MakeHeaderButton(string glyph, string tip, RoutedEventHandler onClick)
        {
            var b = new Button
            {
                Content = glyph,
                FontFamily = iconFont,
                FontSize = 11,
                Width = 26,
                Height = 24,
                ToolTip = tip,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Focusable = false,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.Click += onClick;
            DockPanel.SetDock(b, Dock.Right);
            return b;
        }

        header.Children.Add(MakeHeaderButton("", "關閉", (_, _) => Close()));
        header.Children.Add(MakeHeaderButton("", "檢查 popup（DevTools）", (_, _) =>
        {
            _pin!.IsChecked = true; // 開 DevTools 會讓 popup 失去焦點，先釘選
            _webView?.CoreWebView2?.OpenDevToolsWindow();
        }));

        _pin = new ToggleButton
        {
            Content = "",
            FontFamily = iconFont,
            FontSize = 11,
            Width = 26,
            Height = 24,
            ToolTip = "釘選（失去焦點時不關閉）",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
        };
        DockPanel.SetDock(_pin, Dock.Right);
        header.Children.Add(_pin);

        header.Children.Add(new TextBlock
        {
            Text = item.PopupUrl,
            FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
            FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource("SubtleBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 6, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        root.Children.Add(header);

        _webView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
        root.Children.Add(_webView);
        Content = root;

        Deactivated += OnDeactivated;
        Closed += (_, _) =>
        {
            if (Current == this)
            {
                Current = null;
            }
            _lastClosedId = _item.Id;
            _lastClosedAt = DateTime.UtcNow;
            try
            {
                _webView.Dispose();
            }
            catch
            {
            }
        };
    }

    /// <summary>在指定元素下方（右對齊）顯示 popup。</summary>
    public static async Task ShowForAsync(ExtensionItem item, FrameworkElement anchor, Window owner)
    {
        if (item.PopupUrl == null)
        {
            return;
        }

        // 同一個擴充功能再按一次 = 關閉
        // （點工具列時 popup 會先因失去焦點而關閉，這裡用時間判斷是不是剛關掉的同一個）
        if (Current == null && _lastClosedId == item.Id && (DateTime.UtcNow - _lastClosedAt).TotalMilliseconds < 500)
        {
            _lastClosedId = null;
            return;
        }
        if (Current != null)
        {
            var same = Current._item.Id == item.Id;
            Current.Close();
            if (same)
            {
                return;
            }
        }

        var popup = new ExtensionPopupWindow(item) { Owner = owner };
        Current = popup;

        var source = PresentationSource.FromVisual(anchor);
        var bottomRight = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        if (source?.CompositionTarget != null)
        {
            bottomRight = source.CompositionTarget.TransformFromDevice.Transform(bottomRight);
        }
        popup._anchorRight = bottomRight.X;
        popup._anchorTop = bottomRight.Y + 4;
        popup.PlaceAtAnchor();
        popup.Show();

        try
        {
            var env = await BrowserEnvironment.GetAsync();
            await popup._webView.EnsureCoreWebView2Async(env);
            var core = popup._webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            // popup 呼叫 window.close() 是正常行為（Chrome 允許）；延後關閉，避免在事件回呼中 Dispose WebView2
            core.WindowCloseRequested += (_, _) => popup.Dispatcher.BeginInvoke(() =>
            {
                if (!popup._closing)
                {
                    popup.Close();
                }
            });
            core.NewWindowRequested += (_, e) =>
            {
                // popup 裡的連結開到主視窗新分頁
                e.Handled = true;
                if (owner is IBrowserShell shell)
                {
                    shell.OpenInNewTab(e.Uri);
                }
            };
            core.NavigationCompleted += async (_, _) => await popup.FitToContentAsync();
            core.Navigate(item.PopupUrl);
        }
        catch (Exception ex)
        {
            AppPaths.Log("開啟 popup 失敗：" + ex);
            if (!popup._closing)
            {
                popup.Close();
            }
            MessageBox.Show(owner, "無法開啟 popup：" + ExtensionManager.Describe(ex), "ExtHost", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_pin.IsChecked == true || _closing)
        {
            return;
        }
        // 延後關閉，避免與點擊工具列按鈕（切換開關）互相干擾
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closing && !IsActive)
            {
                Close();
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void PlaceAtAnchor()
    {
        var work = SystemParameters.WorkArea;
        var left = _anchorRight - Width;
        left = Math.Max(work.Left, Math.Min(left, work.Right - Width));
        var top = Math.Min(_anchorTop, Math.Max(work.Top, work.Bottom - Height));
        Left = left;
        Top = top;
    }

    private async Task FitToContentAsync()
    {
        // 頁面可能非同步渲染，量三次
        foreach (var delay in new[] { 0, 250, 900 })
        {
            if (_closing)
            {
                return;
            }
            if (delay > 0)
            {
                await Task.Delay(delay);
            }
            try
            {
                var core = _webView.CoreWebView2;
                if (core == null)
                {
                    return;
                }
                const string script =
                    "(function(){var d=document.documentElement,b=document.body;" +
                    "var w=Math.max(d.scrollWidth,b?b.scrollWidth:0);" +
                    "var h=Math.max(d.scrollHeight,b?b.scrollHeight:0);" +
                    "return [w,h];})()";
                var json = await core.ExecuteScriptAsync(script);
                var arr = JsonSerializer.Deserialize<double[]>(json);
                if (arr is { Length: 2 } && !_closing)
                {
                    var w = Math.Clamp(arr[0] + 2, MinW, MaxW);
                    var h = Math.Clamp(arr[1] + 2 + HeaderH, MinH + HeaderH, MaxH + HeaderH);
                    if (Math.Abs(w - Width) > 1 || Math.Abs(h - Height) > 1)
                    {
                        Width = w;
                        Height = h;
                        PlaceAtAnchor();
                    }
                }
            }
            catch
            {
                // 頁面還沒準備好或已關閉
            }
        }
    }
}
