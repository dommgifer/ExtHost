using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace ExtHost.Services;

/// <summary>擴充功能在 UI 上的呈現資料。</summary>
public sealed class ExtensionItem : INotifyPropertyChanged
{
    private static readonly Color[] BadgeColors =
    {
        Color.FromRgb(0x1F, 0x5F, 0xAD), Color.FromRgb(0x2E, 0x7D, 0x6B), Color.FromRgb(0xB4, 0x53, 0x09),
        Color.FromRgb(0x6D, 0x4A, 0xB0), Color.FromRgb(0xA3, 0x2F, 0x5C), Color.FromRgb(0x3B, 0x6E, 0x22),
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; init; } = "";
    public string? Id { get; set; }
    public string? Path { get; set; }
    public CoreWebView2BrowserExtension? Native { get; set; }
    public ManifestInfo? Manifest { get; set; }

    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsInstalled { get; set; }
    public bool IsEnabled { get; set; }
    public string? LastError { get; set; }
    public ImageSource? Icon { get; set; }
    public List<CompatChip> Chips { get; set; } = new();

    public bool HasPopup => IsInstalled && IsEnabled && Id != null && Manifest?.PopupPage != null;
    public bool HasOptions => IsInstalled && IsEnabled && Id != null && Manifest?.OptionsPage != null;
    public bool HasPath => Path != null;
    public bool HasError => !string.IsNullOrEmpty(LastError);
    public bool HasIcon => Icon != null;
    public bool IsDimmed => !IsInstalled || !IsEnabled;
    public string DisplayId => Id ?? "（尚未載入）";
    public string DisplayPath => Path ?? "（路徑不明：不是由 ExtHost 載入的擴充功能）";
    public string StatusText => !IsInstalled ? "未載入" : IsEnabled ? "" : "已停用";
    public bool HasStatus => StatusText.Length > 0;

    public string BadgeLetter => string.IsNullOrEmpty(Name) ? "?" : char.ToUpperInvariant(Name.Trim()[0]).ToString();

    public Brush BadgeBrush
    {
        get
        {
            var hash = 0;
            foreach (var c in Name)
            {
                hash = unchecked(hash * 31 + c);
            }
            var brush = new SolidColorBrush(BadgeColors[Math.Abs(hash % BadgeColors.Length)]);
            brush.Freeze();
            return brush;
        }
    }

    public bool HasSidePanel => IsInstalled && IsEnabled && Id != null && Manifest?.SidePanelPage != null;
    public string? SidePanelUrl => HasSidePanel ? $"chrome-extension://{Id}/{Manifest!.SidePanelPage}" : null;
    public string? PopupUrl => HasPopup ? $"chrome-extension://{Id}/{Manifest!.PopupPage}" : null;
    public string? OptionsUrl => HasOptions ? $"chrome-extension://{Id}/{Manifest!.OptionsPage}" : null;

    public string ToolTip
    {
        get
        {
            var t = $"{Name} {Version}";
            if (!HasPopup && HasSidePanel)
            {
                t += "\n（點擊開啟側邊欄）";
            }
            else if (!HasPopup)
            {
                t += "\n（沒有 popup，點擊開啟選單）";
            }
            return t;
        }
    }

    public void LoadIcon()
    {
        Icon = null;
        var file = Manifest?.IconFile;
        if (file == null || !File.Exists(file))
        {
            return;
        }
        try
        {
            // 讀進記憶體，避免鎖住檔案（開發時要能直接改圖示）
            var bytes = File.ReadAllBytes(file);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.DecodePixelWidth = 64;
            bmp.EndInit();
            bmp.Freeze();
            Icon = bmp;
        }
        catch
        {
            Icon = null;
        }
    }

    public void NotifyAll() => OnPropertyChanged(string.Empty);

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
