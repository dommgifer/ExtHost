using Microsoft.Web.WebView2.Core;

namespace ExtHost.Services;

/// <summary>所有分頁與 popup 共用的 WebView2 環境（同一個使用者資料夾 → 共用 Cookie、登入狀態、擴充功能）。</summary>
public static class BrowserEnvironment
{
    private static Task<CoreWebView2Environment>? _task;

    public static Task<CoreWebView2Environment> GetAsync() => _task ??= CreateAsync();

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        AppPaths.EnsureCreated();
        var options = new CoreWebView2EnvironmentOptions
        {
            AreBrowserExtensionsEnabled = true,
            Language = "zh-TW",
        };
        return await CoreWebView2Environment.CreateAsync(null, AppPaths.UserData, options);
    }

    public static string? RuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch
        {
            return null;
        }
    }
}
