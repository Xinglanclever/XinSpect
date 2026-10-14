using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 伺服器角色條件式第二波（SV-003／SV-005／SV-008）的單元測試。
/// 三項的核心契約一致：<b>探針回 null＝角色不在（NotApplicable），擲例外＝讀取失敗（ReadError）</b>，
/// 兩者不得互相冒充。
/// </summary>
public class ServerRoleConditional2FactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    private static ServerRoleConditionalFactsService.AddsState Adds(
        bool dc = true, bool running = true, string level = "7")
        => new(dc, running, level);

    // ── SV-003 AD DS ─────────────────────────────────────────────────────

    [Fact]
    public void AD網站控制站_陳述層級與狀態()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            addsProbe: () => Adds(running: true, level: "7")), "sv.adds");

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("網域控制站", f.Value);
        Assert.Contains("NTDS 服務執行中", f.Value);
        Assert.Contains("網域功能層級 7", f.Value);
    }

    [Fact]
    public void AD服務未執行時明說不是執行中()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            addsProbe: () => Adds(running: false)), "sv.adds");

        Assert.Contains("已安裝但未執行", f.Value);
        Assert.DoesNotContain("NTDS 服務執行中", f.Value.Replace("已安裝但未執行", ""));
    }

    [Fact]
    public void AD不列帳號站台或複寫夥伴()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            addsProbe: () => Adds()), "sv.adds");

        // 紅線：只陳述角色，不搬目錄內容。
        Assert.Contains("不列網域帳號", f.Value);
        Assert.Contains("不列站台", f.Value);
        Assert.Contains("不列複寫夥伴", f.Value);
    }

    [Fact]
    public void AD層級讀不到不冒充未知()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            addsProbe: () => Adds(level: "")), "sv.adds");

        // 「讀不到」必須與「未知」分開——留白勝過填一個聽起來像答案的字。
        Assert.Contains("讀不到", f.Value);
        Assert.DoesNotContain("未知", f.Value);
    }

    [Fact]
    public void AD非網域控制站是N_A不是錯誤()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            addsProbe: () => null), "sv.adds");

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不是網域控制站", f.UnavailableReason);
        Assert.Contains("不是缺陷", f.UnavailableReason);
    }

    [Fact]
    public void AD查詢失敗是ReadError不是不適用()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            addsProbe: () => throw new InvalidOperationException("boom")), "sv.adds");

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("boom", f.UnavailableReason);
    }

    // ── SV-005 DHCP ──────────────────────────────────────────────────────

    [Fact]
    public void DHCP陳述範圍與介面數()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            dhcpProbe: () => new ServerRoleConditionalFactsService.DhcpState(true, 3, 2)), "sv.dhcp");

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(3, f.NumericValue);
        Assert.Contains("授權範圍 3 個", f.Value);
        Assert.Contains("繫結介面 2 個", f.Value);
    }

    [Fact]
    public void DHCP零範圍是值不是缺席()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            dhcpProbe: () => new ServerRoleConditionalFactsService.DhcpState(true, 0, 0)), "sv.dhcp");

        // 角色在、服務在跑，只是還沒設範圍：0 是量到的值。
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(0, f.NumericValue);
    }

    [Fact]
    public void DHCP不列配發紀錄與用戶端識別()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            dhcpProbe: () => new ServerRoleConditionalFactsService.DhcpState(true, 1, 1)), "sv.dhcp");

        Assert.Contains("不列配發紀錄", f.Value);
        Assert.Contains("不列用戶端 MAC", f.Value);
    }

    [Fact]
    public void DHCP角色不在是不適用()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            dhcpProbe: () => null), "sv.dhcp");

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("沒有 DHCP 伺服器角色", f.UnavailableReason);
        // 工作站取得位址與扮演 DHCP 伺服器是兩件事——理由必須講清楚免得誤讀。
        Assert.Contains("別人家的", f.UnavailableReason);
    }

    [Fact]
    public void DHCP查詢失敗是ReadError()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            dhcpProbe: () => throw new InvalidOperationException("dhcp-fail")), "sv.dhcp");

        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── SV-008 RDS ───────────────────────────────────────────────────────

    [Fact]
    public void RDS陳述子角色與工作階段數()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            rdsProbe: () => new ServerRoleConditionalFactsService.RdsState(true, true, 4)), "sv.rds");

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(4, f.NumericValue);
        Assert.Contains("工作階段主機", f.Value);
        Assert.Contains("授權", f.Value);
        Assert.Contains("4 個", f.Value);
    }

    [Fact]
    public void RDS授權子角色只陳述存在不讀金鑰()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            rdsProbe: () => new ServerRoleConditionalFactsService.RdsState(true, true, 0)), "sv.rds");

        // L3 禁項：金鑰與授權數永不進值。
        Assert.Contains("只陳述存在", f.Value);
        Assert.Contains("L3 禁項", f.Value);
    }

    [Fact]
    public void RDS不列連線使用者()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            rdsProbe: () => new ServerRoleConditionalFactsService.RdsState(true, false, 2)), "sv.rds");

        Assert.Contains("不列連線使用者", f.Value);
    }

    [Fact]
    public void RDS子角色都認不出時明說未辨識()
    {
        // SessionHost=false 且 Licensing=false 才是「都認不出」；只把 Licensing 設為 false 仍會認出工作階段主機。
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            rdsProbe: () => new ServerRoleConditionalFactsService.RdsState(false, false, 0)), "sv.rds");

        Assert.Contains("子角色未辨識", f.Value);
    }

    [Fact]
    public void RDS只有工作階段主機時不列授權警語()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            rdsProbe: () => new ServerRoleConditionalFactsService.RdsState(true, false, 2)), "sv.rds");

        Assert.Contains("工作階段主機", f.Value);
        // 沒裝授權子角色就不該出現授權金鑰的警語——警語本身也是事實，不能張冠李戴。
        Assert.DoesNotContain("L3 禁項", f.Value);
    }

    [Fact]
    public void RDS角色不在是不適用且說明連入不等於裝角色()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            rdsProbe: () => null), "sv.rds");

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("RDS 角色", f.UnavailableReason);
        Assert.Contains("連入", f.UnavailableReason);
    }

    [Fact]
    public void RDS查詢失敗是ReadError()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            rdsProbe: () => throw new InvalidOperationException("rds-fail")), "sv.rds");

        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── 共同契約 ─────────────────────────────────────────────────────────

    [Fact]
    public void 收集點回七把鍵()
    {
        // 6 → 7（2026-10-14）：SV-010 容錯移轉叢集。同一個「角色條件式」收集點，
        // 契約相同（null＝角色不在、擲例外＝讀取失敗），所以併進這裡而不是另開服務——
        // 先前另開一個同型服務被 ServiceOrphanGateTests 判為重複，那次教訓記在 ledger。
        var facts = ServerRoleConditionalFactsService.Collect(At);
        Assert.Equal(7, facts.Count);
        Assert.Contains(facts, f => f.Key == "sv.adds");
        Assert.Contains(facts, f => f.Key == "sv.dhcp");
        Assert.Contains(facts, f => f.Key == "sv.rds");
        Assert.Contains(facts, f => f.Key == "sv.cluster.state");
    }

    // ── SV-010：容錯移轉叢集 ──────────────────────────────────────────────

    [Fact]
    public void 叢集角色不在是不適用不是零節點()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At, clusterProbe: () => null),
            "sv.cluster.state");

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("不適用", f.UnavailableReason);
        // 「沒裝角色」與「裝了但 0 個節點」必須是兩件不同的事
        Assert.DoesNotContain("0 個節點", f.Value);
    }

    [Fact]
    public void 叢集節點數讀不到是留白不是填零()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            clusterProbe: () => new ServerRoleConditionalFactsService.ClusterState(true, true, null)),
            "sv.cluster.state");

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("留白", f.Value);
        Assert.Contains("不填 0", f.Value);
    }

    [Fact]
    public void 叢集節點數讀得到才給數值()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            clusterProbe: () => new ServerRoleConditionalFactsService.ClusterState(true, false, 3)),
            "sv.cluster.state");

        Assert.Equal(3, f.NumericValue);
        Assert.Contains("3 個節點", f.Value);
        Assert.Contains("未設為自動啟動", f.Value);
    }

    [Fact]
    public void 叢集不列識別資訊()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            clusterProbe: () => new ServerRoleConditionalFactsService.ClusterState(true, true, 2)),
            "sv.cluster.state");

        // 名稱、節點清單、仲裁設定都是環境識別資訊，值裡必須明說不列
        Assert.Contains("不列叢集名稱", f.Value);
        Assert.Contains("不列節點清單", f.Value);
    }

    [Fact]
    public void 叢集查詢失敗是ReadError()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            clusterProbe: () => throw new InvalidOperationException("cluster-fail")),
            "sv.cluster.state");

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("cluster-fail", f.UnavailableReason);
    }

    [Fact]
    public void 探針擲例外不影響其他來源()
    {
        var facts = ServerRoleConditionalFactsService.Collect(At,
            switchesProbe: () => null,
            appPoolsProbe: () => null,
            dnsZonesProbe: () => null,
            addsProbe: () => throw new InvalidOperationException("x"),
            dhcpProbe: () => null,
            rdsProbe: () => new ServerRoleConditionalFactsService.RdsState(true, false, 1),
            clusterProbe: () => new ServerRoleConditionalFactsService.ClusterState(true, true, 1));

        Assert.Equal(FactAvailability.ReadError, Fact(facts, "sv.adds").Availability);
        Assert.Equal(FactAvailability.NotApplicable, Fact(facts, "sv.dhcp").Availability);
        Assert.Equal(FactAvailability.Present, Fact(facts, "sv.rds").Availability);
    }

    [Fact]
    public void 不適用不帶數值而現值帶數值()
    {
        var facts = ServerRoleConditionalFactsService.Collect(At,
            addsProbe: () => null,
            dhcpProbe: () => new ServerRoleConditionalFactsService.DhcpState(true, 2, 1));

        Assert.Null(Fact(facts, "sv.adds").NumericValue);
        Assert.Equal(2, Fact(facts, "sv.dhcp").NumericValue);
    }
}
