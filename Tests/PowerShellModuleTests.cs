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
            "XinSpect.psm1 缺 UTF-8 BOM——PowerShell 5.1 會把無 BOM 的 UTF-8 當 ANSI 讀，中文註解全炸");
    }
}
