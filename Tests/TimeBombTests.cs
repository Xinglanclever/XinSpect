using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 時間炸彈守門（docs/PROGRAM-ULTIMATE-2026-10-10.md §4.1／§5.4）：
/// <b>絕對日期資料基準＋相對查詢窗（<c>UtcNow</c>）在同一個測試方法裡相遇，那條測試就跟著日历爛。</b>
/// </summary>
/// <remarks>
/// <para>
/// 已經炸過一次：<c>TrendSentinelServiceTests</c> 的資料基準原本寫死 2026-10-08，配上
/// <c>UtcNow ± 1 天</c> 的查詢窗——寫測試當天樣本確實在窗內，隔天 UTC 05:00 起窗的起點
/// 越過樣本尾端，8 條斷言同時紅，而服務本身沒有壞。
/// </para>
/// <para>
/// <b>為什麼做在方法級而不是檔案級：</b>主綱的檔案級掃描把 <c>BenchLogTests</c> 與
/// <c>HistoryAndEventsTests</c> 也列為高危，實查兩個檔案裡的絕對日期與 <c>UtcNow</c>
/// 分別在<b>不相干的_helper 與不相干的測試</b>裡，同一個方法沒有相遇——檔案級只有誤報、
/// 抓不到真炸彈，所以這一條的檢查單位是方法。
/// </para>
/// <para>
/// <b>掃描器的等級：</b>啟發式——以 <c>[Fact]／[Theory]</c> 簽名為起點、括號配對取方法體。
/// 字串與註解裡的括號可能讓邊界跑偏，但偏差不會放過「同方法雙信號」這一條（信號本身是
/// 子字串比對），最壞是把相鄰方法併進來多報——多報會紅燈、由人去判，不會靜默放過。
/// 正對照測試（<see cref="掃描器抓得到雙信號方法_正對照"/>）釘住它真有牙。
/// </para>
/// </remarks>
public class TimeBombTests
{
    private static readonly Regex AbsoluteDate = new(@"new\s+DateTime(?:Offset)?\(\s*20\d{2}", RegexOptions.Compiled);
    private static readonly Regex FactMethod = new(
        @"\[(?:Fact|Theory)[^\]]*\]\s*(?:public\s+|private\s+|internal\s+|static\s+)*(?:async\s+)?(?:void|Task)\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    /// <summary>回傳同時含「絕對日期」與「UtcNow 查詢窗」的測試方法名。</summary>
    internal static List<string> ScanSource(string source)
    {
        var offenders = new List<string>();
        foreach (Match m in FactMethod.Matches(source))
        {
            int signatureEnd = m.Index + m.Length;
            int arrow = source.IndexOf("=>", signatureEnd, StringComparison.Ordinal);
            int brace = source.IndexOf('{', signatureEnd);
            if (brace < 0) continue;
            if (arrow >= 0 && arrow < brace) continue;   // 運算式主體：回呼裡沒有宣告資料基準的空間

            int depth = 0, i = brace;
            while (i < source.Length)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) break;
                }
                i++;
            }
            string body = source.Substring(brace, i - brace + 1);
            if (AbsoluteDate.IsMatch(body) && body.Contains("UtcNow", StringComparison.Ordinal))
                offenders.Add(m.Groups[1].Value);
        }
        return offenders;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到倉庫根（XinSpect.csproj）。");
    }

    [Fact]
    public void 測試方法不得同時含絕對日期與UtcNow查詢窗()
    {
        var testsDir = Path.Combine(RepoRoot(), "Tests");
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(testsDir, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                              && !Path.GetFileName(f).Equals("TimeBombTests.cs", StringComparison.Ordinal))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            foreach (var name in ScanSource(File.ReadAllText(file)))
                offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)}::{name}");
        }

        Assert.True(offenders.Count == 0,
            "以下測試方法在同一個方法體裡同時寫了絕對日期與 UtcNow——這是會跟著日历爛的時間炸彈，" +
            "把資料基準改成相對時間（例如 DateTime.UtcNow.AddMinutes(-筆數)）：" +
            string.Join("、", offenders));
    }

    [Fact]
    public void 掃描器抓得到雙信號方法_正對照()
    {
        const string bomb = """
            [Fact]
            public void 那顆炸過的炸彈()
            {
                var t0 = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
                var window = DateTime.UtcNow.AddDays(-1);
                Assert.True(t0 > window);
            }
            """;
        Assert.Equal(["那顆炸過的炸彈"], ScanSource(bomb));

        const string clean = """
            [Fact]
            public void 相對時間寫法()
            {
                var t0 = DateTime.UtcNow.AddMinutes(-9);
                Assert.True(t0 < DateTime.UtcNow);
            }

            [Fact]
            public void 純合成基準不碰查詢窗()
            {
                var origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                Assert.Equal(2026, origin.Year);
            }
            """;
        Assert.Empty(ScanSource(clean));
    }
}
