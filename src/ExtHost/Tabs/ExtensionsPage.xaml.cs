using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ExtHost.Services;

namespace ExtHost.Tabs;

public partial class ExtensionsPage : UserControl
{
    private readonly IBrowserShell _shell;
    private ExtensionManager? _boundManager;

    public ExtensionsPage(IBrowserShell shell)
    {
        _shell = shell;
        InitializeComponent();
        DataFolderText.Text = "資料位置：" + AppPaths.Root + (AppPaths.IsPortable ? "（可攜模式）" : "");
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        DevToggle.IsChecked = _shell.Settings.DevMode;
        WatchToggle.IsChecked = _shell.Settings.WatchExtensionFolders;
        ReloadAllButton.Visibility = _shell.Settings.DevMode ? Visibility.Visible : Visibility.Collapsed;

        var mgr = _shell.Extensions;
        if (mgr != _boundManager)
        {
            if (_boundManager != null)
            {
                _boundManager.Changed -= OnManagerChanged;
            }
            _boundManager = mgr;
            if (mgr != null)
            {
                mgr.Changed += OnManagerChanged;
                Cards.ItemsSource = mgr.Items;
            }
        }
        ApplyFilter();
    }

    private void OnManagerChanged(object? sender, EventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var mgr = _shell.Extensions;
        EmptyText.Visibility = mgr == null || mgr.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (mgr == null)
        {
            return;
        }
        var view = CollectionViewSource.GetDefaultView(mgr.Items);
        var q = SearchBox.Text.Trim();
        view.Filter = q.Length == 0
            ? null
            : o => o is ExtensionItem i
                   && (i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                       || i.Description.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                       || (i.Id?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private static ExtensionItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ExtensionItem;

    private async void LoadUnpacked_Click(object sender, RoutedEventArgs e) => await _shell.LoadUnpackedWithDialogAsync();

    private void DropZone_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => LoadUnpacked_Click(sender, e);

    private async void ReloadAll_Click(object sender, RoutedEventArgs e)
    {
        if (_shell.Extensions != null)
        {
            await _shell.Extensions.ReloadAllAsync();
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.Root);

    private void DevToggle_Click(object sender, RoutedEventArgs e)
    {
        _shell.Settings.DevMode = DevToggle.IsChecked == true;
        _shell.Settings.Save();
        _shell.ApplyDevMode();
        Refresh();
    }

    private void WatchToggle_Click(object sender, RoutedEventArgs e)
    {
        _shell.Settings.WatchExtensionFolders = WatchToggle.IsChecked == true;
        _shell.Settings.Save();
        _shell.Extensions?.UpdateWatchers();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void EnableToggle_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (item == null || _shell.Extensions == null)
        {
            return;
        }
        var enable = (sender as CheckBox)?.IsChecked == true;
        await _shell.Extensions.SetEnabledAsync(item, enable);
    }

    private void Popup_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (item != null)
        {
            _shell.ShowExtensionPopup(item, (FrameworkElement)sender);
        }
    }

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (item?.OptionsUrl != null)
        {
            _shell.OpenInNewTab(item.OptionsUrl);
        }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (item?.Path != null)
        {
            OpenFolder(item.Path);
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (item != null && _shell.Extensions != null)
        {
            await _shell.Extensions.ReloadAsync(item);
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (item == null || _shell.Extensions == null)
        {
            return;
        }
        var r = MessageBox.Show(Window.GetWindow(this)!,
            $"要移除「{item.Name}」嗎？\n\n只會從 ExtHost 移除，不會刪除你的原始碼資料夾。",
            "移除擴充功能", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.OK)
        {
            await _shell.Extensions.RemoveAsync(item);
        }
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (_shell.Extensions == null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }
        foreach (var p in paths)
        {
            var folder = Directory.Exists(p) ? p : Path.GetFileName(p).Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(p) : null;
            if (folder == null)
            {
                _shell.ShowError("請拖入資料夾（或 manifest.json 檔案）：" + p);
                continue;
            }
            try
            {
                await _shell.Extensions.LoadUnpackedAsync(folder);
            }
            catch (Exception ex)
            {
                _shell.ShowError(ex.Message);
            }
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
        }
    }
}
