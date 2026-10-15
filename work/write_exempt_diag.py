# -*- coding: utf-8 -*-
"""診斷：用 C# 端真正的 Exemptions 字典，印出不符門檻的條目。

Python 解析 C# 的續行字串太脆弱，改成寫一個臨時 xUnit 測試把判斷結果印出來。
"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')

diag = '''using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

public class ExemptionDiag
{
    [Fact]
    public void 診斷_印出不符門檻的豁免()
    {
        string[] allowed = ["無法交叉", "沒有第二", "單一來源", "同一份資料", "自己對自己", "無從量測"];
        string[] pending = ["待辦", "未寫", "尚未", "先放", "不重要", "TODO", "之後再"];
        var bad = new List<string>();
        foreach (var (key, reason) in FactCoverageReport.Exemptions)
        {
            if (pending.Any(p => reason.Contains(p, StringComparison.OrdinalIgnoreCase)))
                bad.Add($"[待辦] {key} :: {reason}");
            else if (!allowed.Any(a => reason.Contains(a, StringComparison.Ordinal)))
                bad.Add($"[其他] {key} :: {reason}");
        }
        System.IO.File.WriteAllLines(
            System.IO.Path.Combine(FindRoot(), "work", "exempt_bad.txt"),
            new[] { $"共 {FactCoverageReport.Exemptions.Count} 條豁免，不符門檻 {bad.Count} 條" }.Concat(bad));
        Assert.True(true);
    }

    private static string FindRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        return dir!.FullName;
    }
}
'''
io.open('Tests/ExemptionDiag.cs', 'w', encoding='utf-8', newline='').write(diag)
print('已寫入 Tests/ExemptionDiag.cs')
