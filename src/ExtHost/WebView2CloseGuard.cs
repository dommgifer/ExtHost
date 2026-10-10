using System.Reflection;
using ExtHost.Services;
using Microsoft.Web.WebView2.Wpf;

namespace ExtHost;

/// <summary>
/// WPF 的 WebView2 控制項在 EnsureCoreWebView2Async 時會自己訂閱 WindowCloseRequested，
/// 網頁一呼叫 window.close()，就無條件關閉控制項所在的整個 WPF 視窗（ExtHost 的主視窗）。
/// 而且它比 ExtHost 自己的處理先執行，導致「只關分頁、最後一個分頁先補開新分頁」的規則來不及介入。
/// 這裡在初始化完成後把它的處理移除，window.close() 一律交給 ExtHost 自己決定。
/// </summary>
public static class WebView2CloseGuard
{
    private const string HandlerName = "CoreWebView2_WindowCloseRequested";
    private static MethodInfo? _handler;
    private static bool _searched;

    /// <summary>在 EnsureCoreWebView2Async 完成之後呼叫。</summary>
    public static void DetachDefaultHandler(WebView2 webView)
    {
        var core = webView.CoreWebView2;
        if (core == null)
        {
            return;
        }
        var method = FindHandler(webView.GetType());
        if (method == null)
        {
            AppPaths.Log("找不到 WebView2 控制項內建的 window.close() 處理（SDK 可能已變更），網頁的 window.close() 可能會關閉整個視窗");
            return;
        }
        try
        {
            var handler = (EventHandler<object>)Delegate.CreateDelegate(typeof(EventHandler<object>), webView, method);
            core.WindowCloseRequested -= handler;
        }
        catch (Exception ex)
        {
            AppPaths.Log("移除 WebView2 控制項內建的 window.close() 處理失敗：" + ex.Message);
        }
    }

    private static MethodInfo? FindHandler(Type type)
    {
        if (_searched)
        {
            return _handler;
        }
        for (var t = type; t != null && _handler == null; t = t.BaseType)
        {
            _handler = t.GetMethod(HandlerName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                binder: null, types: new[] { typeof(object), typeof(object) }, modifiers: null);
        }
        _searched = true;
        return _handler;
    }
}
