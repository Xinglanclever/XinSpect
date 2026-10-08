using System.Collections.Generic;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 已安裝角色與功能的判讀（純函式）。核心要釘住的是：
/// 裝了的角色佔磁碟、拉長維護、增加可被利用的程式碼面積；
/// 而「服務沒在跑」不等於「不會跑」——服務可以由相依、RPC 或觸發條件被拉起來。
/// 判讀只陳述事實，不建議移除任何東西（哪個角色有用只有使用者知道）。
/// </summary>
public class RoleSurfaceJudgeTests
{
    private static FeatureEntry F(string name, string display, bool installed,
        int running = 0, int stopped = 0)
        => new(name, display, installed, RoleSurfaceJudge.IsServerRoleName(name), running, stopped);

    // ── 角色名稱辨識 ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("Hyper-V")]
    [InlineData("Failover-Clustering")]
    [InlineData("FS-FileServer")]
    [InlineData("AD-Domain-Services")]
    [InlineData("DNS")]
    [InlineData("DHCP")]
    [InlineData("Multipath-IO")]
    [InlineData("Storage-Replica")]
    [InlineData("Windows-Server-Backup")]
    [InlineData("Containers")]
    public void 伺服器角色_要認出來(string name)
        => Assert.True(RoleSurfaceJudge.IsServerRoleName(name));

    [Theory]
    [InlineData("Microsoft-Windows-Subsystem-Linux")]
    [InlineData("XPS-Viewer")]
    [InlineData("WoW64-Support")]
    [InlineData("Notepad")]
    public void 一般用戶端功能_不得當成伺服器角色(string name)
        => Assert.False(RoleSurfaceJudge.IsServerRoleName(name));

    // ── 已安裝且服務在跑 ──────────────────────────────────────────────────

    [Fact]
    public void 有角色且服務在跑_要點名並提醒()
    {
        var v = RoleSurfaceJudge.Judge(
        [
            F("Hyper-V", "Hyper-V", true, running: 2),
            F("FS-FileServer", "檔案伺服器", true, running: 1),
            F("XPS-Viewer", "XPS 檢視器", true),
        ]);

        Assert.Equal(2, v.InstalledRoles.Count);
        Assert.Equal(2, v.RunningCount);
        Assert.True(v.Attention);
        Assert.Contains("2 個伺服器角色", v.Headline);
        Assert.Contains("Hyper-V", v.Headline);
        Assert.Contains("正在執行", v.Evidence);
    }

    [Fact]
    public void 判讀不得建議移除任何角色()
    {
        var v = RoleSurfaceJudge.Judge([F("Hyper-V", "Hyper-V", true, running: 2)]);

        Assert.Contains("不建議移除", v.Evidence);
        Assert.Contains("只有使用者知道", v.Evidence);
    }

    // ── 裝了但服務沒在跑：最容易誤讀的情況 ────────────────────────────────

    [Fact]
    public void 角色裝了但服務停著_要明說不等於沒有風險()
    {
        // 本機實況的形狀：Hyper-V 裝了、vmms 執行中但 hypervisor 沒載入
        var v = RoleSurfaceJudge.Judge(
        [
            F("Hyper-V", "Hyper-V", true, running: 0, stopped: 3),
            F("Containers", "容器", true, running: 0, stopped: 1),
        ]);

        Assert.False(v.Attention);   // 沒有服務在跑 → 不給提醒
        Assert.Contains("都沒有在執行", v.Evidence);
        Assert.Contains("不等於「不會跑」", v.Evidence);
        Assert.Contains("不等於「沒有風險」", v.Evidence);
    }

    [Fact]
    public void 服務停著時_仍要說出安裝本身的成本()
    {
        var v = RoleSurfaceJudge.Judge([F("Failover-Clustering", "容錯移轉叢集", true)]);

        Assert.Contains("磁碟空間", v.Evidence);
        Assert.Contains("程式碼面積", v.Evidence);
        Assert.Contains("不是錯誤", v.Evidence);
    }

    // ── 沒有角色 ──────────────────────────────────────────────────────────

    [Fact]
    public void 只有一般功能_判為沒有伺服器角色類()
    {
        var v = RoleSurfaceJudge.Judge(
        [
            F("XPS-Viewer", "XPS 檢視器", true),
            F("WoW64-Support", "WoW64 支援", true),
        ]);

        Assert.Empty(v.InstalledRoles);
        Assert.Contains("沒有伺服器角色類", v.Headline);
        Assert.Contains("用戶端功能", v.Evidence);
    }

    [Fact]
    public void 全部未安裝_如實說並說明這是正常的()
    {
        var v = RoleSurfaceJudge.Judge(
        [
            F("Hyper-V", "Hyper-V", false),
            F("DNS", "DNS 伺服器", false),
        ]);

        Assert.Contains("沒有安裝任何", v.Headline);
        Assert.Contains("正常的", v.Evidence);
        Assert.False(v.Attention);
    }

    [Fact]
    public void 沒有功能清單_判為讀不到()
    {
        var v = RoleSurfaceJudge.Judge([]);
        Assert.Contains("讀不到", v.Headline);
    }

    // ── 不得誤導 ──────────────────────────────────────────────────────────

    [Fact]
    public void 任何情況_Headline與Evidence都不得為空()
    {
        var sets = new List<FeatureEntry>[]
        {
            [F("Hyper-V", "Hyper-V", true, running: 1)],
            [F("Hyper-V", "Hyper-V", true)],
            [F("XPS-Viewer", "XPS 檢視器", true)],
            [F("DNS", "DNS", false)],
            [],
        };
        foreach (var s in sets)
        {
            var v = RoleSurfaceJudge.Judge(s);
            Assert.False(string.IsNullOrWhiteSpace(v.Headline));
            Assert.False(string.IsNullOrWhiteSpace(v.Evidence));
        }
    }

    [Fact]
    public void 未安裝的角色_不得出現在已安裝清單裡()
    {
        var v = RoleSurfaceJudge.Judge(
        [
            F("Hyper-V", "Hyper-V", true, running: 1),
            F("DNS", "DNS 伺服器", false),
            F("DHCP", "DHCP 伺服器", false),
        ]);

        Assert.Single(v.InstalledRoles);
        Assert.Contains("Hyper-V", v.InstalledRoles);
        Assert.DoesNotContain("DNS 伺服器", v.InstalledRoles);
    }
}
