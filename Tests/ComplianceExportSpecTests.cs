using System;
using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 合規匯出規格（docs/spec/compliance-export.v1.md）的守門：
/// 文件與程式都必須把 Unverifiable 寫成「不是通過」，且格式名稱不能漂移。
/// </summary>
public class ComplianceExportSpecTests
{
    private static string Spec()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
                return File.ReadAllText(Path.Combine(dir.FullName, "docs", "spec", "compliance-export.v1.md"));
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("找不到原始碼樹（往上找不到 XinSpect.csproj）");
    }

    [Fact]
    public void 匯出規格要涵蓋五種格式與不可洗綠的判定()
    {
        string spec = Spec();

        Assert.Contains("JSON", spec, StringComparison.Ordinal);
        Assert.Contains("SARIF 2.1.0", spec, StringComparison.Ordinal);
        Assert.Contains("JUnit XML", spec, StringComparison.Ordinal);
        Assert.Contains("CEF", spec, StringComparison.Ordinal);
        Assert.Contains("OTLP/JSON", spec, StringComparison.Ordinal);

        Assert.Contains("$schema", spec, StringComparison.Ordinal);
        Assert.Contains(ComplianceExportService.SarifVersion, spec, StringComparison.Ordinal);
        Assert.Contains("timeUnixNano", spec, StringComparison.Ordinal);
        Assert.Contains("severityNumber", spec, StringComparison.Ordinal);
        Assert.Contains("CEF:0|XinSpect|XinSpect|", spec, StringComparison.Ordinal);

        Assert.Contains("`Unverifiable` 永遠不是通過", spec, StringComparison.Ordinal);
        Assert.Contains("不得被寫成 skipped、INFO 或「無 finding」", spec, StringComparison.Ordinal);
        Assert.Contains("`NotSatisfied`／`Unverifiable`／`Exposed`／`Unknown`", spec, StringComparison.Ordinal);
        Assert.Contains("compliance-diff", spec, StringComparison.Ordinal);
        Assert.Contains("`Regressed`", spec, StringComparison.Ordinal);
        Assert.Contains("含變成 `Unverifiable`", spec, StringComparison.Ordinal);
    }
}