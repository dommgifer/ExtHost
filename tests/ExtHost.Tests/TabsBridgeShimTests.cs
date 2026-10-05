using System.Diagnostics;
using Xunit;

namespace ExtHost.Tests;

/// <summary>
/// 以 node 執行 TabsBridgeShim.test.js，驗證注入 popup／側邊欄的 tabs-bridge.js。
/// 需要 PATH 中有 node（GitHub Actions 的 windows-latest 已內建）。
/// </summary>
public sealed class TabsBridgeShimTests
{
    [Fact]
    public void TabsQueryShim_PassesRegressionTests()
    {
        var dir = AppContext.BaseDirectory;
        var test = Path.Combine(dir, "TabsBridgeShim.test.js");
        var shim = Path.Combine(dir, "Scripts", "tabs-bridge.js");

        var psi = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(test);
        psi.ArgumentList.Add(shim);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動 node");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("找不到 node，請安裝 Node.js 後再執行測試", ex);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(60_000), "node 執行逾時");
            Assert.True(process.ExitCode == 0, stdout.Result + stderr.Result);
        }
    }
}
