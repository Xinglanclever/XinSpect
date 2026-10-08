using System;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 虛擬化平台判讀（純函式）。這一組測試釘住的是一個很容易講錯的區分：
/// <b>「元件裝了」「服務在跑」「虛擬層載入了」是三件獨立的事</b>，
/// 一般工具把它們壓成一句「Hyper-V：已啟用／停用」，於是「裝了但沒開」永遠被講錯。
/// 另外也要釘住「虛擬層不是只有 Hyper-V 會拉起來」——VBS／WSL2／容器都會。
/// </summary>
public class VirtualizationJudgeTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    private static VirtualizationState State(
        bool hyper = false, string vendor = "", bool platform = false, bool vmms = false,
        int vms = 0, int vswitches = 0, bool hns = false, bool containers = false, bool wsl = false,
        int vbs = 0, int tasks = 220, int sysTasks = 30)
        => new(hyper, vendor, platform, vmms, vms, vswitches, hns, containers, wsl, vbs, tasks, sysTasks);

    // ── 四種處境必須分得開 ────────────────────────────────────────────────

    [Fact]
    public void 全關_判為裸機且MSR讀值原生()
    {
        var v = VirtualizationJudge.Judge(State());

        Assert.Equal(VirtualizationJudge.VirtKind.BareMetal, v.Kind);
        Assert.True(v.MsrReadable);
        Assert.False(v.Attention);
        Assert.Contains("原生", v.Headline);
    }

    [Fact]
    public void 裝了服務在跑但虛擬層未載入_判為已安裝未載入_這是本機實際處境()
    {
        // Windows Server 上「Hyper-V 功能裝了、vmms 開機啟動、但 bcdedit hypervisorlaunchtype=Off」
        // 是常見組合：不會有 VM 能跑、不佔資源，但服務與攻擊面是開著的。
        var v = VirtualizationJudge.Judge(State(platform: true, vmms: true, vms: 0, vswitches: 0));

        Assert.Equal(VirtualizationJudge.VirtKind.InstalledNotLoaded, v.Kind);
        Assert.True(v.Attention);          // 值得看一眼：以為開了其實沒開
        Assert.True(v.MsrReadable);        // 虛擬層沒載入 → MSR 讀值仍是原生
        Assert.Contains("無法執行任何虛擬機器", v.Headline);
        Assert.Contains("hypervisorlaunchtype", v.Evidence);
    }

    [Fact]
    public void 未載入時_不得說成已啟用虛擬化()
    {
        var v = VirtualizationJudge.Judge(State(platform: true, vmms: true));

        Assert.DoesNotContain("已啟用", v.Headline);
        Assert.DoesNotContain("執行中，", v.Headline);
        Assert.Contains("未載入", v.Headline);
    }

    [Fact]
    public void 平台未用但VBS開啟_虛擬層仍可能被載入()
    {
        // 從未開過 Hyper-V，但記憶體完整性開啟 → VBS 需要虛擬層
        var v = VirtualizationJudge.Judge(State(vbs: 2));

        Assert.Equal(VirtualizationJudge.VirtKind.VbsOnly, v.Kind);
        Assert.False(v.MsrReadable);
        Assert.Contains("VBS", v.Headline);
    }

    [Fact]
    public void 虛擬層載入且有VM_判為執行中且MSR不可信()
    {
        var v = VirtualizationJudge.Judge(State(hyper: true, vendor: "Microsoft Hv",
            platform: true, vmms: true, vms: 3, vswitches: 2, vbs: 2));

        Assert.Equal(VirtualizationJudge.VirtKind.Running, v.Kind);
        Assert.False(v.MsrReadable);
        Assert.Contains("3 台", v.Headline);
        Assert.Contains("Microsoft Hyper-V", v.Evidence);
        Assert.Contains("虛擬化", v.Evidence);   // 必須提醒 MSR 讀值可能被攔截
    }

    [Fact]
    public void 虛擬層載入但沒有VM_要說明不是只有HyperV會拉起來()
    {
        // WSL2／容器／VBS 都會載入虛擬層，此時 VM 數是 0
        var v = VirtualizationJudge.Judge(State(hyper: true, vendor: "Microsoft Hv",
            platform: true, vmms: true, vms: 0, hns: true, containers: true));

        Assert.Equal(VirtualizationJudge.VirtKind.LoadedWithoutVms, v.Kind);
        Assert.False(v.MsrReadable);
        Assert.Contains("VBS", v.Evidence);
        Assert.Contains("hns", v.Evidence);
        Assert.Contains("不是只有", v.Evidence);
    }

    [Fact]
    public void 讀不到任何欄位_判為未知而不是裸機()
    {
        // 把「查不到」當成「沒有」是最容易犯的錯
        var v = VirtualizationJudge.Judge(new VirtualizationState(
            HypervisorLoaded: false, HypervisorVendor: "", PlatformInstalled: false,
            VmmsRunning: false, VmCount: -1, VirtualSwitchCount: -1, HnsRunning: false,
            ContainersFeature: false, WslFeature: false, VbsEnabled: -1, TaskCount: -1,
            TaskRunsUnderSystem: -1));

        Assert.Equal(VirtualizationJudge.VirtKind.Unknown, v.Kind);
        Assert.Contains("讀不到", v.Headline);
    }

    [Fact]
    public void VBS狀態讀不到_不得當成關閉()
    {
        // -1＝查不到。若當成 0（關閉），會讓後面的裸機判定變成不成立的結論。
        var v = VirtualizationJudge.Judge(State(vbs: -1, tasks: -1, sysTasks: -1));

        // 沒有其他跡象時仍是裸機，但依據欄不得出現「VBS 關閉（狀態碼 -1）」這種假陳述
        if (v.Kind == VirtualizationJudge.VirtKind.BareMetal)
            Assert.DoesNotContain("VBS 關閉", v.Evidence);
    }

    // ── 每一種分類都要有話可說、且不得寫入 ────────────────────────────────

    [Fact]
    public void 任何分類_Headline與Evidence都不得為空()
    {
        var states = new[]
        {
            State(),
            State(platform: true, vmms: true),
            State(vbs: 1),
            State(vbs: 2),
            State(hyper: true, vendor: "KVMKVMKVM", vms: 5),
            State(hyper: true, vendor: "Microsoft Hv"),
        };
        foreach (var s in states)
        {
            var v = VirtualizationJudge.Judge(s);
            Assert.False(string.IsNullOrWhiteSpace(v.Headline));
            Assert.False(string.IsNullOrWhiteSpace(v.Evidence));
        }
    }

    [Fact]
    public void 判讀不得寫入_只讀不寫()
    {
        // 這個類別的存在前提是「不碰設定」。公開介面不得出現任何寫入動詞。
        Assert.All(typeof(VirtualizationJudge).GetMethods(),
            m => Assert.DoesNotContain("Set", m.Name, StringComparison.OrdinalIgnoreCase));
        Assert.All(typeof(VirtualizationJudge).GetMethods(),
            m => Assert.DoesNotContain("Write", m.Name, StringComparison.OrdinalIgnoreCase));
        Assert.All(typeof(VirtualizationJudge).GetMethods(),
            m => Assert.DoesNotContain("Enable", m.Name, StringComparison.OrdinalIgnoreCase));
    }

    // ── 事實收集：值與三態 ────────────────────────────────────────────────

    [Fact]
    public void 未載入虛擬層_判讀事實為Present且標明未載入()
    {
        // 沒載入虛擬層不代表「不適用」——這正是本機的處境，必須看得見
        var facts = VirtualizationFactsService.Collect(At,
            () => new VirtualizationState(false, "", true, true, 0, 0, false, true, false, 0, 220, 30));

        var judge = Assert.Single(facts, f => f.Key == "virt.judge");
        Assert.Equal(FactAvailability.Present, judge.Availability);
        Assert.Contains("未載入", judge.Value);

        var vmcount = Assert.Single(facts, f => f.Key == "virt.vmcount");
        Assert.Equal(FactAvailability.NotApplicable, vmcount.Availability);
        Assert.Contains("未定義任何虛擬機器", vmcount.UnavailableReason);

        var msr = Assert.Single(facts, f => f.Key == "virt.msr");
        Assert.Contains("是", msr.Value);   // 虛擬層未載入 → MSR 讀值原生
    }

    [Fact]
    public void 有VM時_判讀事實標為Present且數量入NumericValue()
    {
        var facts = VirtualizationFactsService.Collect(At,
            () => new VirtualizationState(true, "Microsoft Hv", true, true, 3, 2, false, false, false, 2, 220, 30));

        var vmcount = Assert.Single(facts, f => f.Key == "virt.vmcount");
        Assert.Equal(3, vmcount.NumericValue);

        var vendor = Assert.Single(facts, f => f.Key == "virt.vendor");
        Assert.Equal("Microsoft Hv", vendor.Value);      // 事實帶原始簽章，廠商名由判讀層翻

        var judge = Assert.Single(facts, f => f.Key == "virt.judge");
        Assert.Contains("Microsoft Hyper-V", judge.Value);
        Assert.Equal("否（虛擬層可能攔截或改寫）", Assert.Single(facts, f => f.Key == "virt.msr").Value);
    }

    [Fact]
    public void 讀不到VM數_事實為ReadError而非零()
    {
        // 讀不到回 -1。若當成 0 陳列，會出現「已定義的虛擬機器：0 台」這種假事實
        var facts = VirtualizationFactsService.Collect(At,
            () => new VirtualizationState(false, "", false, false, -1, -1, false, false, false, -1, -1, -1));

        Assert.Equal(FactAvailability.ReadError, Assert.Single(facts, f => f.Key == "virt.vmcount").Availability);
        Assert.Equal(FactAvailability.ReadError, Assert.Single(facts, f => f.Key == "virt.vswitch").Availability);
        Assert.Equal(FactAvailability.ReadError, Assert.Single(facts, f => f.Key == "virt.vbs").Availability);
    }

    [Fact]
    public void 事實收集擲回例外_以ReadError回報且不拋出()
    {
        var facts = VirtualizationFactsService.Collect(At, () => throw new InvalidOperationException("模擬失敗"));

        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("模擬失敗", f.UnavailableReason);
    }

    // ── WMI 列舉：主機自己那一筆不得算成虛擬機器 ──────────────────────────

    [Theory]
    [InlineData("Hosting Computer System")]
    [InlineData("Microsoft Hosting Computer System")]
    [InlineData("主機電腦系統")]           // 繁中 Windows 的實際回報（本機）
    [InlineData("Microsoft 主機電腦系統")]
    [InlineData("主机电脑系统")]           // 簡中
    public void 主機自己那一筆_中英文都要辨識出來(string caption)
        => Assert.True(VirtualizationFactsService.IsHostingComputerSystem(caption));

    [Theory]
    [InlineData("虛擬機器")]
    [InlineData("MyTestVM")]
    [InlineData("HOSTING-lite")]           // 只沾到字根、不是那一筆
    public void 真正的虛擬機器_不得被當成主機自己(string caption)
        => Assert.False(VirtualizationFactsService.IsHostingComputerSystem(caption));
}
