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
    private const string Shim = """
        (() => {
          const tabs = globalThis.chrome && globalThis.chrome.tabs;
          if (!tabs || typeof tabs.query !== 'function' || tabs.__exthostPatched) {
            return;
          }
          const original = tabs.query.bind(tabs);
          let activeUrl = null;
          globalThis.__exthostSetActiveTabUrl = url => { activeUrl = url || null; };

          const stripHash = u => (u || '').split('#')[0];
          const urlOf = t => t.url || t.pendingUrl || '';
          const findActive = list => {
            if (!activeUrl) {
              return null;
            }
            return list.find(t => urlOf(t) === activeUrl)
              || list.find(t => stripHash(urlOf(t)) === stripHash(activeUrl))
              || null;
          };

          async function query(info) {
            info = info || {};
            const wantsCurrent = info.currentWindow === true || info.lastFocusedWindow === true || info.windowId === -2;
            if (!wantsCurrent) {
              return original(info);
            }
            const rest = Object.assign({}, info);
            delete rest.currentWindow;
            delete rest.lastFocusedWindow;
            delete rest.windowId;
            delete rest.active;
            const self = location.href;
            const all = (await original(rest)).filter(t => urlOf(t) !== self);
            const active = findActive(all);
            const marked = all.map(t => Object.assign({}, t, { active: t === active, highlighted: t === active }));
            if (info.active === true) {
              return marked.filter(t => t.active);
            }
            if (info.active === false) {
              return marked.filter(t => !t.active);
            }
            return marked;
          }

          const patched = function (info, callback) {
            const p = query(info);
            if (typeof callback === 'function') {
              p.then(r => callback(r), e => { console.error(e); callback([]); });
              return undefined;
            }
            return p;
          };
          try {
            tabs.query = patched;
          } catch (e) {
          }
          if (tabs.query !== patched) {
            try {
              Object.defineProperty(tabs, 'query', { value: patched, configurable: true, writable: true });
            } catch (e) {
              console.warn('[ExtHost] 無法改寫 chrome.tabs.query', e);
              return;
            }
          }
          tabs.__exthostPatched = true;
        })();
        """;

    private readonly CoreWebView2 _core;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _urlScriptId;
    private string? _activeUrl;

    private ExtensionTabsBridge(CoreWebView2 core) => _core = core;

    /// <summary>在 Navigate 之前呼叫，之後每次載入頁面都會套用。</summary>
    public static async Task<ExtensionTabsBridge> InstallAsync(CoreWebView2 core, string? activeUrl)
    {
        var bridge = new ExtensionTabsBridge(core);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Shim);
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
