using System.Windows;
using ExtHost.Services;

namespace ExtHost.Tabs;

/// <summary>擴充功能管理頁分頁（WPF 原生畫面，不是網頁）。</summary>
public sealed class ExtensionsTab : TabBase
{
    private readonly ExtensionsPage _page;

    public ExtensionsTab(IBrowserShell shell)
    {
        _page = new ExtensionsPage(shell);
        Title = "擴充功能管理";
        Url = UrlHelper.ExtensionsPageUrl;
    }

    public override FrameworkElement View => _page;

    public override string Glyph => "";

    public override void OnActivated() => _page.Refresh();
}
