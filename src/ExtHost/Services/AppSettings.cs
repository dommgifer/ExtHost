using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExtHost.Services;

public sealed class RegisteredExtension
{
    /// <summary>未封裝擴充功能的資料夾（含 manifest.json）。</summary>
    public string Path { get; set; } = "";

    /// <summary>上次成功載入時 WebView2 給的 ID。</summary>
    public string? Id { get; set; }
}

public sealed class AppSettings
{
    /// <summary>新分頁開啟的網址。</summary>
    public string HomePage { get; set; } = "about:blank";

    /// <summary>網址列輸入非網址時的搜尋網址，{0} 會被替換成關鍵字。</summary>
    public string SearchUrl { get; set; } = "https://www.google.com/search?q={0}";

    /// <summary>開發人員模式：顯示「重載擴充」按鈕等開發功能。</summary>
    public bool DevMode { get; set; } = true;

    /// <summary>擴充功能資料夾有檔案變更時自動重新載入。</summary>
    public bool WatchExtensionFolders { get; set; } = true;

    /// <summary>重新載入擴充功能後，一併重新整理目前分頁。</summary>
    public bool ReloadTabAfterExtensionReload { get; set; } = true;

    /// <summary>
    /// 擴充功能重新載入方式：
    /// "reinstall" = 移除後重新加入（一定會讀到新檔案，但該擴充功能的 chrome.storage 可能被清除）
    /// "toggle"    = 停用再啟用（保留資料，但部分變更可能不會生效）
    /// </summary>
    public string ReloadMode { get; set; } = "reinstall";

    /// <summary>啟動時還原上次開啟的分頁。</summary>
    public bool RestoreSession { get; set; } = true;

    public List<string> LastSession { get; set; } = new();

    public List<RegisteredExtension> Extensions { get; set; } = new();

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("讀取 settings.json 失敗：" + ex.Message);
            try
            {
                File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".broken", true);
            }
            catch
            {
            }
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            AppPaths.Log("儲存 settings.json 失敗：" + ex.Message);
        }
    }
}
