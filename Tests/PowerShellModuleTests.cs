using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// PowerShell 模組（WP32）的契約：正確呼叫 CLI 引數、退出碼 2 走警告不走 throw、匯出函式名稱穩定。
/// 模組檔須帶 UTF-8 BOM（PowerShell 5.1 對無 BOM 的 UTF-8 會當 ANSI 讀，中文全炸）——機器檢查。
/// </summary>
public class PowerShellModuleTests
{
    private static string ModulePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("找不到 repo 根"), "XinSpect.psm1");
    }

    private static string ModuleText() => File.ReadAllText(ModulePath());

    [Fact]
    public void 模組呼叫CLI的正確引數與退出碼語意()
    {
        var text = ModuleText();

        Assert.Contains("\"--json\", \"evidence\"", text, StringComparison.Ordinal);
        Assert.Contains("\"--query\"", text, StringComparison.Ordinal);
        Assert.Contains("\"--out\"", text, StringComparison.Ordinal);
        Assert.Contains("$LASTEXITCODE -eq 2", text, StringComparison.Ordinal); // 部分三態走警告
        Assert.Contains("throw", text, StringComparison.Ordinal);               // 致命錯誤走例外
        Assert.Contains("Export-ModuleMember -Function Get-XinSpectEvidence", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 模組檔必須帶UTF8BOM()
    {
        var bytes = File.ReadAllBytes(ModulePath());
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "XinSpect.psm1 缺 UTF-8 BOM——PowerShell 5.1 對無 BOM 的 UTF-8 會當 ANSI 讀，中文全炸");
    }

    // ── 逐域 cmdlet（U10-PF-0017／ps.modules.gap）────────────────────────

    [Fact]
    public void 逐域cmdlet_全數定義且匯出_每個都包CLI_headless呼叫()
    {
        string text = ModuleText();
        string[] cmdlets =
        [
            "Get-XinSpectCpu", "Get-XinSpectMemory", "Get-XinSpectMotherboard",
            "Get-XinSpectFirmware", "Get-XinSpectStorage", "Get-XinSpectGpu",
            "Get-XinSpectNetwork", "Get-XinSpectPower", "Get-XinSpectOs",
            "Get-XinSpectSecurity", "Get-XinSpectTests",
        ];
        foreach (string cmdlet in cmdlets)
        {
            Assert.Contains($"function {cmdlet}", text, StringComparison.Ordinal);
            Assert.Contains($"'{cmdlet}'", text, StringComparison.Ordinal);   // 有進 Export-ModuleMember
        }
        // 逐域 cmdlet 的實作路徑只有一條：Get-XinSpectEvidence → XinSpect.exe --json evidence。
        Assert.Contains("InvokeDomain", text, StringComparison.Ordinal);
        int coreUses = System.Text.RegularExpressions.Regex.Matches(text, @"& \$ExePath @cliArgs").Count;
        Assert.Equal(1, coreUses);   // 唯一一次直接呼叫 CLI 在 Get-XinSpectEvidence 裡——域 cmdlet 不準繞過包裝
    }

    [Fact]
    public void 逐域前綴_只列程式碼裡真有鍵的前綴()
    {
        // 查無鍵的前綴不得寫進說明——把不存在的東西說成會輸出，就是誤導。
        string text = ModuleText();
        foreach (string dead in new[] { "'wifi.'", "'typec.'", "'nfc.'", "'thermal.'" })
            Assert.DoesNotContain(dead, text, StringComparison.Ordinal);
        Assert.Contains("ps.modules.gap", text, StringComparison.Ordinal);   // 追蹤鍵留在原始碼裡供掃描
    }
}
