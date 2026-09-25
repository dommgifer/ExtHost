using System.Windows;
using ExtHost.Services;

namespace ExtHost;

/// <summary>擴充功能管理頁等元件呼叫主視窗功能的介面。</summary>
public interface IBrowserShell
{
    AppSettings Settings { get; }
    ExtensionManager? Extensions { get; }
    void OpenInNewTab(string url);
    void ShowExtensionPopup(ExtensionItem item, FrameworkElement anchor);
    Task LoadUnpackedWithDialogAsync();
    void ApplyDevMode();
    void ShowError(string message);
}
