using System.IO;
using System.Text.Json;

namespace ExtHost.Services;

public sealed record CompatChip(string Text, bool IsWarning, string Tooltip);

/// <summary>解析 manifest.json 取得名稱、popup、選項頁、圖示、權限等資訊。</summary>
public sealed class ManifestInfo
{
    public string Name { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string Description { get; private set; } = "";
    public int ManifestVersion { get; private set; }
    public string? PopupPage { get; private set; }
    public string? OptionsPage { get; private set; }

    /// <summary>側邊欄頁面（manifest 的 side_panel.default_path）。</summary>
    public string? SidePanelPage { get; private set; }

    /// <summary>側邊欄頁面是不是由常見檔名推測出來的（manifest 沒寫 default_path）。</summary>
    public bool SidePanelGuessed { get; private set; }
    public string? IconFile { get; private set; }
    public bool HasAction { get; private set; }
    public List<string> Permissions { get; } = new();
    public List<string> HostPermissions { get; } = new();
    public List<MatchPattern> ContentScriptMatches { get; } = new();
    public List<string> ManifestKeys { get; } = new();

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // WebView2 沒有瀏覽器 UI，下列 API 大多依賴瀏覽器介面，標示為「需實測」
    private static readonly Dictionary<string, string> RiskyPermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["contextMenus"] = "右鍵選單需要瀏覽器 UI，WebView2 可能不支援",
        ["sidePanel"] = "側邊欄需要瀏覽器 UI，WebView2 不支援",
        ["tabs"] = "tabs API 在 WebView2 中僅部分可用（每個分頁是獨立的 WebView2）",
        ["tabGroups"] = "分頁群組需要瀏覽器 UI",
        ["windows"] = "windows API 在 WebView2 中行為不同",
        ["notifications"] = "系統通知可能不支援",
        ["downloads"] = "downloads API 可能不支援",
        ["bookmarks"] = "WebView2 沒有書籤",
        ["history"] = "history API 可能不支援",
        ["identity"] = "identity API 可能不支援",
        ["topSites"] = "WebView2 沒有熱門網站",
        ["sessions"] = "sessions API 可能不支援",
        ["search"] = "search API 可能不支援",
        ["tts"] = "tts API 可能不支援",
        ["offscreen"] = "offscreen API 需實測",
        ["debugger"] = "debugger API 需實測",
    };

    private static readonly Dictionary<string, string> RiskyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["side_panel"] = "側邊欄需要瀏覽器 UI，WebView2 不支援",
        ["commands"] = "鍵盤快捷鍵需要瀏覽器 UI，可能不會觸發",
        ["omnibox"] = "網址列關鍵字需要瀏覽器 UI，不支援",
        ["devtools_page"] = "DevTools 擴充頁面需實測",
        ["chrome_url_overrides"] = "新分頁/書籤頁覆寫不支援",
    };

    private static readonly HashSet<string> SafePermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        "storage", "unlimitedStorage", "alarms", "scripting", "activeTab", "cookies", "webRequest",
        "declarativeNetRequest", "declarativeNetRequestWithHostAccess", "declarativeNetRequestFeedback",
        "clipboardRead", "clipboardWrite", "idle",
    };

    public static ManifestInfo Load(string folder)
    {
        var file = Path.Combine(folder, "manifest.json");
        if (!File.Exists(file))
        {
            throw new InvalidOperationException("資料夾內找不到 manifest.json：" + folder);
        }

        string text;
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs))
        {
            text = sr.ReadToEnd();
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, DocOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"manifest.json 格式錯誤（第 {ex.LineNumber + 1} 行）：{ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var info = new ManifestInfo();

            foreach (var prop in root.EnumerateObject())
            {
                info.ManifestKeys.Add(prop.Name);
            }

            info.ManifestVersion = root.TryGetProperty("manifest_version", out var mv) && mv.ValueKind == JsonValueKind.Number
                ? mv.GetInt32()
                : 0;

            var messages = LoadMessages(folder, root);
            info.Name = Localize(GetString(root, "name") ?? Path.GetFileName(folder), messages);
            info.Version = GetString(root, "version") ?? "";
            info.Description = Localize(GetString(root, "description") ?? "", messages);

            JsonElement action = default;
            var hasAction = root.TryGetProperty("action", out action)
                || root.TryGetProperty("browser_action", out action)
                || root.TryGetProperty("page_action", out action);
            info.HasAction = hasAction;

            if (hasAction && action.ValueKind == JsonValueKind.Object)
            {
                var popup = GetString(action, "default_popup");
                if (!string.IsNullOrWhiteSpace(popup))
                {
                    info.PopupPage = popup.TrimStart('/');
                }
                info.IconFile = PickIcon(folder, action.TryGetProperty("default_icon", out var di) ? di : default);
            }

            if (info.IconFile == null && root.TryGetProperty("icons", out var icons))
            {
                info.IconFile = PickIcon(folder, icons);
            }

            if (root.TryGetProperty("options_ui", out var optionsUi) && optionsUi.ValueKind == JsonValueKind.Object)
            {
                info.OptionsPage = GetString(optionsUi, "page")?.TrimStart('/');
            }
            info.OptionsPage ??= GetString(root, "options_page")?.TrimStart('/');

            ReadStringArray(root, "permissions", info.Permissions);
            ReadStringArray(root, "optional_permissions", info.Permissions);
            ReadStringArray(root, "host_permissions", info.HostPermissions);

            // MV2 把 host 權限寫在 permissions 裡
            foreach (var p in info.Permissions.ToList())
            {
                if (p.Contains("://", StringComparison.Ordinal) || p == "<all_urls>")
                {
                    info.Permissions.Remove(p);
                    info.HostPermissions.Add(p);
                }
            }

            // 側邊欄：WebView2 沒有 Side Panel UI，由 ExtHost 在主視窗右側代管
            if (root.TryGetProperty("side_panel", out var sidePanel) && sidePanel.ValueKind == JsonValueKind.Object)
            {
                info.SidePanelPage = GetString(sidePanel, "default_path")?.TrimStart('/');
            }
            if (info.SidePanelPage == null
                && (root.TryGetProperty("side_panel", out _) || info.Permissions.Any(x => x.Equals("sidePanel", StringComparison.OrdinalIgnoreCase))))
            {
                // 沒寫 default_path（改用 chrome.sidePanel.setOptions 動態指定）時，試著找常見檔名
                foreach (var guess in new[] { "sidepanel.html", "side_panel.html", "sidebar.html", "panel.html",
                                              "sidepanel/index.html", "side_panel/index.html", "src/sidepanel/index.html" })
                {
                    if (File.Exists(Path.Combine(folder, guess.Replace('/', Path.DirectorySeparatorChar))))
                    {
                        info.SidePanelPage = guess;
                        info.SidePanelGuessed = true;
                        break;
                    }
                }
            }

            if (root.TryGetProperty("content_scripts", out var cs) && cs.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in cs.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("matches", out var matches)
                        || matches.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    foreach (var m in matches.EnumerateArray())
                    {
                        if (m.ValueKind == JsonValueKind.String)
                        {
                            var mp = MatchPattern.TryParse(m.GetString()!);
                            if (mp != null)
                            {
                                info.ContentScriptMatches.Add(mp);
                            }
                        }
                    }
                }
            }

            return info;
        }
    }

    public bool MatchesUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }
        foreach (var m in ContentScriptMatches)
        {
            if (m.IsMatch(url))
            {
                return true;
            }
        }
        return false;
    }

    public List<CompatChip> BuildCompatChips()
    {
        var chips = new List<CompatChip>();

        if (ManifestKeys.Contains("content_scripts"))
        {
            chips.Add(new CompatChip("content_scripts", false, "頁面注入腳本，可正常使用"));
        }
        if (ManifestKeys.Contains("background"))
        {
            chips.Add(new CompatChip("background", false, "背景 service worker / 背景頁"));
        }

        if (HasAction)
        {
            chips.Add(PopupPage != null
                ? new CompatChip("popup（由工具列代管）", false, "點工具列按鈕會開啟 popup 視窗")
                : SidePanelPage != null
                    ? new CompatChip("側邊欄（由 ExtHost 代管）", false,
                        "點工具列按鈕會在主視窗右側開啟側邊欄：" + SidePanelPage + (SidePanelGuessed ? "（manifest 沒寫 default_path，依檔名推測）" : ""))
                    : new CompatChip("action.onClicked", true, "沒有 default_popup：WebView2 無法觸發 action.onClicked，工具列按鈕只會顯示選單"));
        }

        foreach (var p in Permissions.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (p.Equals("sidePanel", StringComparison.OrdinalIgnoreCase) && SidePanelPage != null)
            {
                chips.Add(new CompatChip("sidePanel · 需實測", true,
                    "側邊欄頁面由 ExtHost 代管；chrome.sidePanel.open / setOptions 等 API 在 WebView2 中可能無效"));
            }
            else if (RiskyPermissions.TryGetValue(p, out var why))
            {
                chips.Add(new CompatChip(p + " · 需實測", true, why));
            }
            else if (SafePermissions.Contains(p))
            {
                chips.Add(new CompatChip(p, false, "一般可用"));
            }
            else
            {
                chips.Add(new CompatChip(p, false, "未列入已知清單，請自行實測"));
            }
        }

        foreach (var key in ManifestKeys)
        {
            if (key.Equals("side_panel", StringComparison.OrdinalIgnoreCase) && SidePanelPage != null)
            {
                continue; // 已在上方標示為由 ExtHost 代管
            }
            if (RiskyKeys.TryGetValue(key, out var why))
            {
                chips.Add(new CompatChip(key + " · 需實測", true, why));
            }
        }

        if (HostPermissions.Count > 0)
        {
            chips.Add(new CompatChip($"主機權限 ×{HostPermissions.Count}", false, string.Join("\n", HostPermissions)));
        }

        return chips;
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static void ReadStringArray(JsonElement root, string name, List<string> into)
    {
        if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in arr.EnumerateArray())
            {
                if (v.ValueKind == JsonValueKind.String)
                {
                    into.Add(v.GetString()!);
                }
            }
        }
    }

    private static string? PickIcon(string folder, JsonElement el)
    {
        string? rel = null;
        if (el.ValueKind == JsonValueKind.String)
        {
            rel = el.GetString();
        }
        else if (el.ValueKind == JsonValueKind.Object)
        {
            var candidates = new List<(int size, string path)>();
            foreach (var p in el.EnumerateObject())
            {
                if (int.TryParse(p.Name, out var size) && p.Value.ValueKind == JsonValueKind.String)
                {
                    candidates.Add((size, p.Value.GetString()!));
                }
            }
            // 優先選 >= 32 的最小尺寸，沒有就選最大
            var pick = candidates.Where(c => c.size >= 32).OrderBy(c => c.size).FirstOrDefault();
            if (pick.path == null)
            {
                pick = candidates.OrderByDescending(c => c.size).FirstOrDefault();
            }
            rel = pick.path;
        }

        if (string.IsNullOrWhiteSpace(rel))
        {
            return null;
        }

        var full = Path.Combine(folder, rel.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? full : null;
    }

    private static Dictionary<string, string> LoadMessages(string folder, JsonElement root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var locales = new List<string> { "zh_TW", "zh_Hant" };
        var def = GetString(root, "default_locale");
        if (def != null)
        {
            locales.Add(def);
        }
        locales.Add("en");

        foreach (var loc in locales)
        {
            var file = Path.Combine(folder, "_locales", loc, "messages.json");
            if (!File.Exists(file))
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), DocOptions);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (!result.ContainsKey(p.Name) && p.Value.ValueKind == JsonValueKind.Object
                        && p.Value.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                    {
                        result[p.Name] = msg.GetString()!;
                    }
                }
            }
            catch
            {
                // 忽略語系檔錯誤
            }
        }
        return result;
    }

    private static string Localize(string value, Dictionary<string, string> messages)
    {
        if (value.StartsWith("__MSG_", StringComparison.Ordinal) && value.EndsWith("__", StringComparison.Ordinal) && value.Length > 8)
        {
            var key = value[6..^2];
            if (messages.TryGetValue(key, out var msg))
            {
                return msg;
            }
        }
        return value;
    }
}
