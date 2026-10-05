using ExtHost.Services;
using Xunit;

namespace ExtHost.Tests;

public sealed class ManifestSidePanelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exthost-manifest-" + Guid.NewGuid().ToString("N"));

    public ManifestSidePanelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private ManifestInfo Load(string manifestJson, params string[] files)
    {
        File.WriteAllText(Path.Combine(_dir, "manifest.json"), manifestJson);
        foreach (var f in files)
        {
            var full = Path.Combine(_dir, f.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "<html></html>");
        }
        return ManifestInfo.Load(_dir);
    }

    [Fact]
    public void SidePanel_DefaultPath_IsParsed()
    {
        var m = Load("""
            { "manifest_version": 3, "name": "x", "version": "1",
              "action": {}, "permissions": ["sidePanel", "tabs"],
              "side_panel": { "default_path": "/panel/main.html" } }
            """);
        Assert.Equal("panel/main.html", m.SidePanelPage);
        Assert.False(m.SidePanelGuessed);

        var chips = m.BuildCompatChips();
        Assert.Contains(chips, c => c.Text.StartsWith("側邊欄") && !c.IsWarning);
        Assert.DoesNotContain(chips, c => c.Text == "action.onClicked");
        Assert.DoesNotContain(chips, c => c.Text.StartsWith("side_panel"));
    }

    [Fact]
    public void SidePanel_WithoutDefaultPath_GuessesCommonFileName()
    {
        var m = Load("""
            { "manifest_version": 3, "name": "x", "version": "1", "permissions": ["sidePanel"] }
            """, "sidepanel.html");
        Assert.Equal("sidepanel.html", m.SidePanelPage);
        Assert.True(m.SidePanelGuessed);
    }

    [Fact]
    public void NoSidePanel_KeepsActionOnClickedWarning()
    {
        var m = Load("""
            { "manifest_version": 3, "name": "x", "version": "1", "action": {} }
            """, "sidepanel.html");
        Assert.Null(m.SidePanelPage);
        Assert.Contains(m.BuildCompatChips(), c => c.Text == "action.onClicked" && c.IsWarning);
    }

    [Fact]
    public void Popup_TakesPrecedenceInChips()
    {
        var m = Load("""
            { "manifest_version": 3, "name": "x", "version": "1",
              "action": { "default_popup": "popup.html" },
              "side_panel": { "default_path": "panel.html" } }
            """);
        Assert.Equal("popup.html", m.PopupPage);
        Assert.Equal("panel.html", m.SidePanelPage);
        Assert.Contains(m.BuildCompatChips(), c => c.Text.StartsWith("popup"));
    }
}
