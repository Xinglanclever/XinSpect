using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 建置警告基線（docs/PROGRAM-ULTIMATE-2026-10-10.md §5.6）：主專案的編譯警告<b>只准下降</b>。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要有這一條：警告沒有基線就和沒有守門一樣——2026-10-10 實測主專案有 13 條
/// （CS8604/CS8629/CS0219/SYSLIB0057…），全部存在了數十個版本而沒人看見，因為沒有東西在數。
/// 清零之後基線設 0：任何人新增一條警告，全套就會紅，紅燈訊息指名是哪條。
/// </para>
/// <para>
/// <b>量法：</b>對主專案跑一次真正的小組建（獨立輸出目錄 <c>obj/_warncheck/</c>，不碰開發中的
/// 建置產物、不鎖 apphost），彙總 stdout＋stderr，以「檔案(行,欄): warning 代碼」去重後計數——
/// MSBuild 會把同一條警告依 pass／_wpftmp 重複輸出 4 次，去重才是真數（實測 52→13）。
/// </para>
/// <para>
/// 這一條很慢（完整重建主專案），所以和執行期對帳共用禁並行集合；它測的是建置而不是執行期，
/// 放在一般並行批次裡也會和其他測試搶 MSBuild 資源。
/// </para>
/// </remarks>
[Collection("RealHardwareReconcile")]
public class WarningBaselineTests
{
    /// <summary>
    /// 基線 = 0：主專案清零之後，任何新增警告都會讓這一條紅。
    /// 只准往下降（沒有可降空間了）；要放寬基線就等於承認新增警告，需要在版本沿革裡寫理由。
    /// </summary>
    private const int Baseline = 0;

    private static readonly Regex WarningLine =
        new(@"([A-Za-z0-9_./\\]+\.cs)\((\d+),(\d+)\): warning ([A-Z]+\d+)", RegexOptions.Compiled);

    [Fact]
    [Trait("Category", "BuildAnalysis")]
    public void 主專案建置警告數不超過基線()
    {
        string root = RepoRoot();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "build XinSpect.csproj -c Debug --nologo -t:Rebuild -v n -p:BaseOutputPath=obj/_warncheck/",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var sb = new System.Text.StringBuilder();
        using (var proc = Process.Start(psi) ?? throw new InvalidOperationException("dotnet build 啟動失敗"))
        {
            sb.Append(proc.StandardOutput.ReadToEnd());
            sb.Append(proc.StandardError.ReadToEnd());
            proc.WaitForExit();
            Assert.True(proc.ExitCode == 0, "建置本身失敗——先看建置錯誤：" + Tail(sb));
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in WarningLine.Matches(sb.ToString()))
            unique.Add($"{m.Groups[1].Value}({m.Groups[2].Value},{m.Groups[3].Value}): {m.Groups[4].Value}");

        Assert.True(unique.Count <= Baseline,
            $"主專案建置警告 {unique.Count} 條，基線 {Baseline}——警告只准下降。明细：\n" + string.Join("\n", unique));
    }

    private static string Tail(System.Text.StringBuilder sb)
    {
        string s = sb.ToString();
        return s.Length <= 4000 ? s : s[^4000..];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到倉庫根（XinSpect.csproj）。");
    }
}
