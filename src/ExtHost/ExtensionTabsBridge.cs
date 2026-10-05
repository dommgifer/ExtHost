using System.IO;
using System.Text.Json;
using ExtHost.Services;
using Microsoft.Web.WebView2.Core;

namespace ExtHost;

/// <summary>
/// 讓側邊欄與 popup 的 chrome.tabs.query 認得 ExtHost 目前選取的分頁。
/// WebView2 把每個 WebView2 當成獨立的視窗（各只有一個分頁、且都是 active），
/// 側邊欄或 popup 呼叫 chrome.tabs.query({ active: true, currentWindow: true }) 時會拿到自己。
/// 這裡在頁面載入前注入腳本：查詢條件含 currentWindow / lastFocusedWindow 時，
/// 改從全部分頁中找出網址等於 ExtHost 目前分頁的那一個。回傳的仍是 WebView2 的真實分頁 id，
/// 因此後續的 chrome.scripting.executeScript、chrome.tabs.sendMessage 都能正常使用。
/// </summary>
public sealed class ExtensionTabsBridge
{
    private static readonly Lazy<string> Shim = new(() =>
    {
        using var stream = typeof(ExtensionTabsBridge).Assembly.GetManifestResourceStream("ExtHost.Scripts.tabs-bridge.js")
            ?? throw new InvalidOperationException("找不到內嵌資源 tabs-bridge.js");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private readonly CoreWebView2 _core;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _urlScriptId;
    private string? _activeUrl;

    private ExtensionTabsBridge(CoreWebView2 core) => _core = core;

    /// <summary>在 Navigate 之前呼叫，之後每次載入頁面都會套用。</summary>
    public static async Task<ExtensionTabsBridge> InstallAsync(CoreWebView2 core, string? activeUrl)
    {
        var bridge = new ExtensionTabsBridge(core);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Shim.Value);
        await bridge.SetActiveTabUrlAsync(activeUrl);
        return bridge;
    }

    /// <summary>ExtHost 目前選取的分頁換了，或分頁網址變了。</summary>
    public async Task SetActiveTabUrlAsync(string? url)
    {
        await _gate.WaitAsync();
        try
        {
            await SetCoreAsync(url);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SetCoreAsync(string? url)
    {
        if (url == _activeUrl && _urlScriptId != null)
        {
            return;
        }
        _activeUrl = url;
        var call = $"globalThis.__exthostSetActiveTabUrl && globalThis.__exthostSetActiveTabUrl({JsonSerializer.Serialize(url)});";
        try
        {
            // 重新載入頁面時使用（排在 Shim 之後執行）
            if (_urlScriptId != null)
            {
                _core.RemoveScriptToExecuteOnDocumentCreated(_urlScriptId);
            }
            _urlScriptId = await _core.AddScriptToExecuteOnDocumentCreatedAsync(call);
            // 已載入的頁面立即更新
            await _core.ExecuteScriptAsync(call);
        }
        catch (Exception ex)
        {
            AppPaths.Log("更新擴充功能目前分頁失敗：" + ex.Message);
        }
    }
}
