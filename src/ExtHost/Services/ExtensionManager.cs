using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace ExtHost.Services;

/// <summary>
/// 管理 WebView2 Profile 內的擴充功能：載入未封裝資料夾、啟用/停用、重新載入、移除、監看檔案變更。
/// 所有 WebView2 呼叫都在 UI 執行緒進行。
/// </summary>
public sealed class ExtensionManager
{
    private readonly CoreWebView2Profile _profile;
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _loadErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DispatcherTimer> _debounce = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<ExtensionItem> Items { get; } = new();

    /// <summary>工具列上顯示的擴充功能（已啟用）。</summary>
    public ObservableCollection<ExtensionItem> ToolbarItems { get; } = new();

    /// <summary>擴充功能清單有變動。</summary>
    public event EventHandler? Changed;

    /// <summary>有擴充功能被重新載入（開發模式下可用來重新整理分頁）。</summary>
    public event EventHandler<ExtensionsReloadedEventArgs>? Reloaded;

    /// <summary>非同步作業發生錯誤，需通知使用者。</summary>
    public event EventHandler<string>? Error;

    public ExtensionManager(CoreWebView2Profile profile, AppSettings settings, Dispatcher dispatcher)
    {
        _profile = profile;
        _settings = settings;
        _dispatcher = dispatcher;
    }

    public int EnabledCount => Items.Count(i => i.IsInstalled && i.IsEnabled);
    public int DisabledCount => Items.Count - EnabledCount;

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var installed = await _profile.GetBrowserExtensionsAsync();
            foreach (var reg in _settings.Extensions.ToList())
            {
                var already = reg.Id != null && installed.Any(e => e.Id == reg.Id);
                if (already)
                {
                    continue;
                }
                if (!Directory.Exists(reg.Path))
                {
                    _loadErrors[reg.Path] = "資料夾不存在";
                    continue;
                }
                await TryAddCoreAsync(reg);
            }
            _settings.Save();
            await RefreshCoreAsync();
        }
        finally
        {
            _gate.Release();
        }

        UpdateWatchers();
    }

    public async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await RefreshCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>載入未封裝的擴充功能資料夾。失敗時丟出例外（訊息可直接顯示）。</summary>
    public async Task<ExtensionItem?> LoadUnpackedAsync(string folder)
    {
        folder = UrlHelper.NormalizeFolder(folder);

        // 先自行檢查 manifest，錯誤訊息比 WebView2 的清楚
        ManifestInfo.Load(folder);

        await _gate.WaitAsync();
        try
        {
            var reg = _settings.Extensions.FirstOrDefault(r => string.Equals(r.Path, folder, StringComparison.OrdinalIgnoreCase));
            var isNew = reg == null;
            reg ??= new RegisteredExtension { Path = folder };

            var installed = await _profile.GetBrowserExtensionsAsync();
            var existing = reg.Id != null ? installed.FirstOrDefault(e => e.Id == reg.Id) : null;
            if (existing != null)
            {
                await existing.RemoveAsync();
            }

            CoreWebView2BrowserExtension ext;
            try
            {
                ext = await _profile.AddBrowserExtensionAsync(folder);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("WebView2 無法載入此擴充功能：" + Describe(ex), ex);
            }

            reg.Id = ext.Id;
            if (isNew)
            {
                _settings.Extensions.Add(reg);
            }
            _loadErrors.Remove(folder);
            _settings.Save();
            await RefreshCoreAsync();
            UpdateWatchers();
            return Items.FirstOrDefault(i => i.Id == ext.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetEnabledAsync(ExtensionItem item, bool enabled)
    {
        await _gate.WaitAsync();
        try
        {
            if (item.Native != null)
            {
                await item.Native.EnableAsync(enabled);
            }
            else if (enabled && item.Path != null)
            {
                // 尚未載入成功的項目：嘗試重新載入
                var reg = FindRegistration(item.Path);
                if (reg != null)
                {
                    await TryAddCoreAsync(reg);
                    _settings.Save();
                }
            }
            await RefreshCoreAsync();
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"變更「{item.Name}」狀態失敗：{Describe(ex)}");
            await RefreshCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReloadAsync(ExtensionItem item)
    {
        if (item.Path == null)
        {
            Error?.Invoke(this, $"「{item.Name}」不是由 ExtHost 載入的，無法得知資料夾位置，不能重新載入。");
            return;
        }
        await ReloadPathAsync(item.Path);
    }

    public async Task ReloadAllAsync()
    {
        var paths = new List<string>();
        await _gate.WaitAsync();
        try
        {
            foreach (var reg in _settings.Extensions.ToList())
            {
                await ReloadCoreAsync(reg);
                paths.Add(reg.Path);
            }
            _settings.Save();
            await RefreshCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
        Reloaded?.Invoke(this, new ExtensionsReloadedEventArgs(paths));
    }

    public async Task RemoveAsync(ExtensionItem item)
    {
        await _gate.WaitAsync();
        try
        {
            if (item.Native != null)
            {
                await item.Native.RemoveAsync();
            }
            if (item.Path != null)
            {
                _settings.Extensions.RemoveAll(r => string.Equals(r.Path, item.Path, StringComparison.OrdinalIgnoreCase));
                _loadErrors.Remove(item.Path);
            }
            else if (item.Id != null)
            {
                _settings.Extensions.RemoveAll(r => r.Id == item.Id);
            }
            _settings.Save();
            await RefreshCoreAsync();
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"移除「{item.Name}」失敗：{Describe(ex)}");
        }
        finally
        {
            _gate.Release();
        }
        UpdateWatchers();
    }

    /// <summary>依設定開啟或關閉資料夾監看。</summary>
    public void UpdateWatchers()
    {
        var wanted = _settings.WatchExtensionFolders
            ? _settings.Extensions.Select(r => r.Path).Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in _watchers.Keys.ToList())
        {
            if (!wanted.Contains(path))
            {
                _watchers[path].Dispose();
                _watchers.Remove(path);
            }
        }

        foreach (var path in wanted)
        {
            if (_watchers.ContainsKey(path))
            {
                continue;
            }
            try
            {
                var w = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                var captured = path;
                FileSystemEventHandler onChange = (_, e) => OnFolderChanged(captured, e.FullPath);
                w.Changed += onChange;
                w.Created += onChange;
                w.Deleted += onChange;
                w.Renamed += (_, e) => OnFolderChanged(captured, e.FullPath);
                w.EnableRaisingEvents = true;
                _watchers[path] = w;
            }
            catch (Exception ex)
            {
                AppPaths.Log($"無法監看資料夾 {path}：{ex.Message}");
            }
        }
    }

    public void Shutdown()
    {
        foreach (var w in _watchers.Values)
        {
            w.Dispose();
        }
        _watchers.Clear();
        foreach (var t in _debounce.Values)
        {
            t.Stop();
        }
    }

    public List<ExtensionItem> MatchingContentScripts(string? url) =>
        Items.Where(i => i.IsInstalled && i.IsEnabled && i.Manifest != null && i.Manifest.MatchesUrl(url)).ToList();

    private void OnFolderChanged(string root, string fullPath)
    {
        // 忽略版本控制與套件資料夾、編輯器暫存檔
        var rel = fullPath.Length > root.Length ? fullPath[root.Length..] : "";
        if (rel.Contains(@"\.git", StringComparison.OrdinalIgnoreCase)
            || rel.Contains(@"\node_modules", StringComparison.OrdinalIgnoreCase)
            || rel.Contains(@"\_metadata", StringComparison.OrdinalIgnoreCase)
            || rel.EndsWith("~", StringComparison.Ordinal)
            || rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || rel.EndsWith(".swp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (!_debounce.TryGetValue(root, out var timer))
            {
                timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(800),
                };
                timer.Tick += async (_, _) =>
                {
                    timer.Stop();
                    AppPaths.Log("偵測到檔案變更，重新載入：" + root);
                    await ReloadPathAsync(root);
                };
                _debounce[root] = timer;
            }
            timer.Stop();
            timer.Start();
        });
    }

    private async Task ReloadPathAsync(string path)
    {
        var paths = new List<string>();
        await _gate.WaitAsync();
        try
        {
            var reg = FindRegistration(path);
            if (reg == null)
            {
                return;
            }
            await ReloadCoreAsync(reg);
            paths.Add(reg.Path);
            _settings.Save();
            await RefreshCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
        Reloaded?.Invoke(this, new ExtensionsReloadedEventArgs(paths));
    }

    private async Task ReloadCoreAsync(RegisteredExtension reg)
    {
        try
        {
            ManifestInfo.Load(reg.Path);
        }
        catch (Exception ex)
        {
            _loadErrors[reg.Path] = ex.Message;
            return;
        }

        var installed = await _profile.GetBrowserExtensionsAsync();
        var existing = reg.Id != null ? installed.FirstOrDefault(e => e.Id == reg.Id) : null;

        if (existing != null && string.Equals(_settings.ReloadMode, "toggle", StringComparison.OrdinalIgnoreCase))
        {
            if (!existing.IsEnabled)
            {
                return;
            }
            try
            {
                await existing.EnableAsync(false);
                await existing.EnableAsync(true);
                _loadErrors.Remove(reg.Path);
            }
            catch (Exception ex)
            {
                _loadErrors[reg.Path] = Describe(ex);
            }
            return;
        }

        var wasEnabled = existing?.IsEnabled ?? true;
        try
        {
            if (existing != null)
            {
                await existing.RemoveAsync();
            }
        }
        catch (Exception ex)
        {
            _loadErrors[reg.Path] = "移除舊版本失敗：" + Describe(ex);
            return;
        }

        var added = await TryAddCoreAsync(reg);
        if (added != null && !wasEnabled)
        {
            try
            {
                await added.EnableAsync(false);
            }
            catch
            {
            }
        }
    }

    private async Task<CoreWebView2BrowserExtension?> TryAddCoreAsync(RegisteredExtension reg)
    {
        try
        {
            ManifestInfo.Load(reg.Path);
            var ext = await _profile.AddBrowserExtensionAsync(reg.Path);
            reg.Id = ext.Id;
            _loadErrors.Remove(reg.Path);
            return ext;
        }
        catch (Exception ex)
        {
            _loadErrors[reg.Path] = Describe(ex);
            AppPaths.Log($"載入擴充功能失敗 {reg.Path}：{ex}");
            return null;
        }
    }

    private RegisteredExtension? FindRegistration(string path) =>
        _settings.Extensions.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    private async Task RefreshCoreAsync()
    {
        IReadOnlyList<CoreWebView2BrowserExtension> installed;
        try
        {
            installed = await _profile.GetBrowserExtensionsAsync();
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, "讀取擴充功能清單失敗：" + Describe(ex));
            return;
        }

        var list = new List<ExtensionItem>();

        foreach (var ext in installed)
        {
            var reg = _settings.Extensions.FirstOrDefault(r => r.Id == ext.Id);
            var item = new ExtensionItem
            {
                Key = "id:" + ext.Id,
                Id = ext.Id,
                Native = ext,
                Path = reg?.Path,
                IsInstalled = true,
                IsEnabled = ext.IsEnabled,
                Name = ext.Name,
            };
            FillFromManifest(item);
            if (reg != null && _loadErrors.TryGetValue(reg.Path, out var err))
            {
                item.LastError = err;
            }
            list.Add(item);
        }

        foreach (var reg in _settings.Extensions)
        {
            if (reg.Id != null && installed.Any(e => e.Id == reg.Id))
            {
                continue;
            }
            var item = new ExtensionItem
            {
                Key = "path:" + reg.Path,
                Id = null,
                Path = reg.Path,
                IsInstalled = false,
                IsEnabled = false,
                Name = System.IO.Path.GetFileName(reg.Path),
                LastError = _loadErrors.TryGetValue(reg.Path, out var err) ? err : "未載入",
            };
            FillFromManifest(item);
            list.Add(item);
        }

        list = list.OrderBy(i => i.Name, StringComparer.CurrentCulture).ToList();

        Items.Clear();
        foreach (var i in list)
        {
            Items.Add(i);
        }

        ToolbarItems.Clear();
        foreach (var i in list.Where(i => i.IsInstalled && i.IsEnabled))
        {
            ToolbarItems.Add(i);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void FillFromManifest(ExtensionItem item)
    {
        if (item.Path == null || !Directory.Exists(item.Path))
        {
            return;
        }
        try
        {
            var m = ManifestInfo.Load(item.Path);
            item.Manifest = m;
            if (!string.IsNullOrWhiteSpace(m.Name))
            {
                item.Name = m.Name;
            }
            item.Version = m.Version;
            item.Description = m.Description;
            item.Chips = m.BuildCompatChips();
            item.LoadIcon();
        }
        catch (Exception ex)
        {
            item.LastError ??= ex.Message;
        }
    }

    public static string Describe(Exception ex)
    {
        var msg = ex.Message;
        if (ex is COMException || ex.HResult != 0 && msg.Length < 10)
        {
            msg += $" (0x{ex.HResult:X8})";
        }
        return msg;
    }
}

/// <summary>重新載入完成；Paths 為這次被重新載入的擴充功能資料夾。</summary>
public sealed class ExtensionsReloadedEventArgs(IReadOnlyList<string> paths) : EventArgs
{
    public IReadOnlyList<string> Paths { get; } = paths;
}
