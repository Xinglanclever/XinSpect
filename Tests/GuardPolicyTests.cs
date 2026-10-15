using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-055（守門模式宣告）、P1-061（白名單上限）、
/// P1-062（守門等級宣告）、P1-063（誤報治理）的守門。
///
/// <para><b>這一組守的是「守門自己」。</b>本專案有 80 多條守門測試，
/// 品質差異極大——有的附正對照、有的只是「跑一次沒炸」。
/// 這一組把守門的規格變成可稽核的問題。
/// </para>
/// </summary>
public class GuardPolicyTests
{
    // ── P1-055 模式宣告 ────────────────────────────────────────────────

    [Fact]
    public void 模式_五種模式每一種都有中文名()
    {
        foreach (var mode in Enum.GetValues<GuardPolicy.GuardMode>())
        {
            string name = GuardPolicy.Describe(mode);
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.DoesNotContain("未收錄", name);
        }
    }

    [Fact]
    public void 模式_宣告不完整時要指名缺什麼()
    {
        var guard = new GuardPolicy.Guard("測試守門", GuardPolicy.GuardMode.Property,
            "", new GuardPolicy.GuardLevel("", ""), HasPositiveControl: false);

        var problems = GuardPolicy.Validate(guard);
        Assert.Contains(problems, p => p.Contains("真實風險"));
        Assert.Contains(problems, p => p.Contains("等級宣告不完整"));
    }

    // ── P1-062 等級宣告 ────────────────────────────────────────────────

    [Fact]
    public void 等級_只寫守得住什麼不算宣告完整()
    {
        // 說不出「守不住什麼」的守門，會讓使用者以為它比實際更嚴密。
        var level = new GuardPolicy.GuardLevel("擋得住直接呼叫", "");
        Assert.False(level.IsDeclared);
    }

    [Fact]
    public void 等級_兩個都寫才完整()
    {
        var level = new GuardPolicy.GuardLevel(
            "擋得住掃描器直接呼叫寫入 API",
            "擋不住「掃描器呼叫另一個會寫入的方法」——那是呼叫圖分析");
        Assert.True(level.IsDeclared);
    }

    [Fact]
    public void 等級_原始碼掃描沒有正對照要被指出來()
    {
        var guard = new GuardPolicy.Guard("掃描守門", GuardPolicy.GuardMode.SourceScan,
            "某種寫法不得出現在生產碼裡", new GuardPolicy.GuardLevel("擋得住 X", "擋不住 Y"),
            HasPositiveControl: false);

        var problems = GuardPolicy.Validate(guard);
        Assert.Contains(problems, p => p.Contains("沒有附正對照"));
        Assert.Contains(problems, p => p.Contains("永遠是綠的"));
    }

    [Fact]
    public void 等級_政策釘死沒有正對照也要被指出來()
    {
        var guard = new GuardPolicy.Guard("政策守門", GuardPolicy.GuardMode.PolicyPin,
            "某個政策值必須維持", new GuardPolicy.GuardLevel("擋得住 X", "擋不住 Y"),
            HasPositiveControl: false);

        Assert.Contains(GuardPolicy.Validate(guard), p => p.Contains("沒有附正對照"));
    }

    [Fact]
    public void 等級_性質類守門不強制正對照()
    {
        // 性質測試本身就是在驗「該成立會成立」，加正對照的意義不同。
        var guard = new GuardPolicy.Guard("性質守門", GuardPolicy.GuardMode.Property,
            "對任意輸入都要成立的關係", new GuardPolicy.GuardLevel("擋得住 X", "擋不住 Y"),
            HasPositiveControl: false);

        Assert.Empty(GuardPolicy.Validate(guard));
    }

    // ── P1-061 白名單上限 ──────────────────────────────────────────────

    [Fact]
    public void 白名單_超過上限時要說先重新想一遍()
    {
        var entries = new Dictionary<string, string>
        {
            ["a"] = "理由夠長的理由夠長夠長",
            ["b"] = "理由夠長的理由夠長夠長",
            ["c"] = "理由夠長的理由夠長夠長",
        };
        var problems = GuardPolicy.ValidateWhitelist(entries, cap: 2);

        // 恰好一筆：超限。三條理由都夠長，不該另外再報。
        Assert.Single(problems);
        Assert.Contains("上限 2", problems[0]);
        Assert.Contains("而不是提高上限", problems[0]);
    }

    [Fact]
    public void 白名單_沒理由或理由太短都要被指出來()
    {
        var entries = new Dictionary<string, string>
        {
            ["空的"] = "",
            ["太短"] = "待辦",
        };
        var problems = GuardPolicy.ValidateWhitelist(entries, cap: 10);

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("空的") && p.Contains("必須寫出理由"));
        Assert.Contains(problems, p => p.Contains("太短") && p.Contains("理由太短"));
    }

    [Fact]
    public void 白名單_在上限內且理由充分時沒有問題()
    {
        var entries = new Dictionary<string, string>
        {
            ["刻意例外"] = "這是刻意分開的另一種狀態，與本守門的範圍無關，理由充分。",
        };
        Assert.Empty(GuardPolicy.ValidateWhitelist(entries, cap: 5));
    }

    // ── P1-063 誤報治理 ────────────────────────────────────────────────

    [Fact]
    public void 誤報_放寬門檻一律不接受()
    {
        string? verdict = GuardPolicy.JudgeFalsePositive(
            GuardPolicy.FalsePositiveAction.LoosenThreshold, "因為它一直紅燈所以放寬到 300");
        Assert.NotNull(verdict);
        Assert.Contains("沒有人會記得它被放寬過", verdict);
    }

    [Fact]
    public void 誤報_其他處置要寫出足夠的理由()
    {
        Assert.NotNull(GuardPolicy.JudgeFalsePositive(
            GuardPolicy.FalsePositiveAction.Whitelist, "太短"));
        Assert.Null(GuardPolicy.JudgeFalsePositive(
            GuardPolicy.FalsePositiveAction.Whitelist,
            "這一項確實不屬於本守門的範圍，因為它來自另一條獨立的資料路徑，已在該處另行守門。"));
    }

    [Fact]
    public void 誤報_修正守門也要寫理由()
    {
        Assert.NotNull(GuardPolicy.JudgeFalsePositive(
            GuardPolicy.FalsePositiveAction.FixGuard, ""));
    }
}
