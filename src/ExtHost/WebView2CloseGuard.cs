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
    private static bool _searched;
    private static FieldInfo? _field;   // 方法所在物件的欄位；null 表示方法直接在控制項上
    private static MethodInfo? _method;

    /// <summary>在 EnsureCoreWebView2Async 完成之後呼叫。</summary>
    public static void DetachDefaultHandler(WebView2 webView)
    {
        var core = webView.CoreWebView2;
        if (core == null)
        {
            return;
        }
        try
        {
            EnsureSearched(webView.GetType());
            if (_method == null)
            {
                AppPaths.Log("找不到 WebView2 控制項內建的 window.close() 處理（SDK 可能已變更），網頁的 window.close() 可能會關閉整個視窗");
                return;
            }
            var target = _field == null ? webView : _field.GetValue(webView);
            if (target == null)
            {
                AppPaths.Log("WebView2 控制項內部物件尚未建立，無法移除內建的 window.close() 處理");
                return;
            }
            var handler = (EventHandler<object>)Delegate.CreateDelegate(typeof(EventHandler<object>), target, _method);
            core.WindowCloseRequested -= handler;
        }
        catch (Exception ex)
        {
            AppPaths.Log("移除 WebView2 控制項內建的 window.close() 處理失敗：" + ex.Message);
        }
    }

    /// <summary>
    /// 找出處理方法所在的物件：
    /// 新版 SDK 的 WebView2 內部以欄位（m_webview2Base）持有 WebView2Base，方法在 WebView2Base 上；
    /// 舊版則直接在 WebView2 或其父類別上。
    /// </summary>
    private static void EnsureSearched(Type controlType)
    {
        if (_searched)
        {
            return;
        }
        _searched = true;

        _method = FindMethod(controlType);
        if (_method != null)
        {
            return;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var sdk = typeof(WebView2).Assembly;
        for (var t = controlType; t != null && t.Assembly == sdk; t = t.BaseType)
        {
            foreach (var field in t.GetFields(flags))
            {
                if (field.FieldType.Assembly != sdk)
                {
                    continue;
                }
                var method = FindMethod(field.FieldType);
                if (method != null)
                {
                    _field = field;
                    _method = method;
                    return;
                }
            }
        }
    }

    private static MethodInfo? FindMethod(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var t = type; t != null; t = t.BaseType)
        {
            var method = t.GetMethod(HandlerName, flags, binder: null, types: new[] { typeof(object), typeof(object) }, modifiers: null);
            if (method != null)
            {
                return method;
            }
        }
        return null;
    }
}
