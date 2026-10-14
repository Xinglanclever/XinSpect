using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>檔案與列印伺服器角色視角（SV-006／SV-007）單元測試。</summary>
public class ServerRoleFilePrintFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    private static ServerRoleFilePrintFactsService.FileSharesState Shares(
        params ServerRoleFilePrintFactsService.ShareRow[] rows)
        => new ServerRoleFilePrintFactsService.FileSharesState(rows, rows.Length);

    // ── 路徑歸類 ─────────────────────────────────────────────────────────

    [Fact]
    public void 路徑歸類_IPC不帶路徑()
        => Assert.Equal(ServerRoleFilePrintFactsService.PathClassIpc,
            ServerRoleFilePrintFactsService.PathClass(3, null));

    [Fact]
    public void 路徑歸類_系統磁碟根以實際系統碟判定()
    {
        string systemRoot = System.IO.Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        // 不假設是 C:——機器上系統碟可能是別的代號，這是本項刻意不寫死的地方。
        Assert.Equal(ServerRoleFilePrintFactsService.PathClassSystemRoot,
            ServerRoleFilePrintFactsService.PathClass(0, systemRoot + "Data"));
    }

    [Fact]
    public void 路徑歸類_使用者目錄看得出來()
    {
        string systemRoot = System.IO.Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        Assert.Equal(ServerRoleFilePrintFactsService.PathClassUserProfile,
            ServerRoleFilePrintFactsService.PathClass(0,
                System.IO.Path.Combine(systemRoot, "Users", "someone", "Documents")));
    }

    [Fact]
    public void 路徑歸類_沒有路徑時是未取得()
        => Assert.Equal(ServerRoleFilePrintFactsService.PathClassUnknown,
            ServerRoleFilePrintFactsService.PathClass(0, null));

    // ── 廣泛授權判定 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("S-1-1-0")]        // Everyone
    [InlineData("S-1-5-32-545")]   // BUILTIN\Users
    [InlineData("S-1-5-11")]       // Authenticated Users
    public void 廣泛授權認得出(string sid)
        => Assert.True(ServerRoleFilePrintFactsService.IsBroadGrant([sid]));

    [Fact]
    public void 個別帳號不算廣泛授權()
        => Assert.False(ServerRoleFilePrintFactsService.IsBroadGrant(["S-1-5-21-1-2-3-1001"]));

    // ── SV-006 共用視角 ──────────────────────────────────────────────────

    [Fact]
    public void 共用_依路徑類別分組且不揭露路徑()
    {
        var f = Fact(ServerRoleFilePrintFactsService.Collect(At, shares: () => Shares(
            new ServerRoleFilePrintFactsService.ShareRow("Data", 0, ServerRoleFilePrintFactsService.PathClassDataVolume, 3, false),
            new ServerRoleFilePrintFactsService.ShareRow("Users", 0, ServerRoleFilePrintFactsService.PathClassUserProfile, 2, true))),
            "sv.file.shares");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("磁碟共用 2 個", f.Value);
        Assert.Contains("資料磁碟 1", f.Value);
        Assert.Contains("使用者目錄 1", f.Value);
        Assert.Equal(2d, f.NumericValue);
    }

    [Fact]
    public void 共用_廣泛授權是風險面不是判決()
    {
        var f = Fact(ServerRoleFilePrintFactsService.Collect(At, shares: () => Shares(
            new ServerRoleFilePrintFactsService.ShareRow("Public", 0, ServerRoleFilePrintFactsService.PathClassDataVolume, 2, true))),
            "sv.file.shares");
        Assert.Contains("含廣泛授權", f.Value);
        Assert.Contains("風險面不是漏洞判決", f.Value);
    }

    [Fact]
    public void 共用_預設系統共用計數()
    {
        var f = Fact(ServerRoleFilePrintFactsService.Collect(At, shares: () => Shares(
            new ServerRoleFilePrintFactsService.ShareRow("C$", 0, ServerRoleFilePrintFactsService.PathClassSystemRoot, 1, false),
            new ServerRoleFilePrintFactsService.ShareRow("ADMIN$", 0, ServerRoleFilePrintFactsService.PathClassSystemRoot, 1, false),
            new ServerRoleFilePrintFactsService.ShareRow("Data", 0, ServerRoleFilePrintFactsService.PathClassDataVolume, 1, false))),
            "sv.file.shares");
        Assert.Contains("預設系統共用 2 個", f.Value);
    }

    [Fact]
    public void 共用_ACL讀不到時不得當成沒有廣泛授權()
    {
        // 三個共用只有一個 ACL 讀得到。
        var state = new ServerRoleFilePrintFactsService.FileSharesState(
            new List<ServerRoleFilePrintFactsService.ShareRow>
            {
                new("A", 0, ServerRoleFilePrintFactsService.PathClassDataVolume, 1, false),
                new("B", 0, ServerRoleFilePrintFactsService.PathClassDataVolume, 1, false),
                new("C", 0, ServerRoleFilePrintFactsService.PathClassDataVolume, 1, false),
            }, 1);
        var f = Fact(ServerRoleFilePrintFactsService.Collect(At, shares: () => state), "sv.file.shares");
        Assert.Contains("2 個共用的 ACL <b>讀不到</b>", f.Value);
        Assert.Contains("未經檢查", f.Value);
        Assert.Contains("不是「沒有廣泛授權」", f.Value);
    }

    [Fact]
    public void 共用_零共用是收斂不是缺陷()
    {
        var f = Fact(ServerRoleFilePrintFactsService.Collect(At, shares: () => Shares()), "sv.file.shares");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(0d, f.NumericValue);
        Assert.Contains("收斂攻擊面", f.Value);
        Assert.Contains("不是缺陷", f.Value);
    }

    [Fact]
    public void 共用_不列個別帳號()
    {
        var f = Fact(ServerRoleFilePrintFactsService.Collect(At, shares: () => Shares(
            new ServerRoleFilePrintFactsService.ShareRow("Data", 0, ServerRoleFilePrintFactsService.PathClassDataVolume, 3, false))),
            "sv.file.shares");
        Assert.Contains("不列個別帳號", f.Value);
    }

    [Fact]
    public void 共用_讀取失敗是ReadError()
    {
        var f = Fact(ServerRoleFilePrintFactsService.Collect(At, shares: () => null), "sv.file.shares");
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不畫成 0 個共用", f.UnavailableReason);
    }

    // ── SV-007 列印視角 ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Microsoft Print to PDF", "Microsoft Print To PDF", "PORTPROMPT:")]
    [InlineData("Microsoft XPS Document Writer", "Microsoft XPS Document Writer v4", "PORTPROMPT:")]
    [InlineData("Fax", "Microsoft Shared Fax Driver", "SHRFAX:")]
    [InlineData("OneNote (Desktop)", "Send to Microsoft OneNote 16 Driver", "nul:")]
    public void 虛擬印表機認得出(string name, string driver, string port)
        => Assert.False(ServerRoleFilePrintFactsService.IsPhysicalPrinter(name, driver, port));

    [Theory]
    [InlineData("HP LaserJet 400", "HP LaserJet 400 PCL 6", "USB001")]
    [InlineData("Brother HL-L2350DW", "Brother HL-L2350DW series", "WSD-abc")]
    [InlineData("Generic / Text Only", "Generic / Text Only", "LPT1:")]
    public void 實體印表機不被誤判(string name, string driver, string port)
        => Assert.True(ServerRoleFilePrintFactsService.IsPhysicalPrinter(name, driver, port));

    [Fact]
    public void 列印_實體與虛擬分開計數()
    {
        var f = Fact(ServerRoleFilePrintFactsService.Collect(At, printers: () =>
        [
            new(true, true, false),
            new(false, false, false),
            new(false, false, false),
        ]), "sv.print.queues");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("印表機 3 台", f.Value);
        Assert.Contains("實體裝置 1 台、虛擬印表機 2 台", f.Value);
        Assert.Equal(1d, f.NumericValue);   // 數值取實體台數
    }

    [Fact]
    public void 列印_全是虛擬時要說清楚()
    {
        var f = Fact(ServerRoleFilePrintFactsService.Collect(At, printers: () =>
            [new(false, false, false), new(false, false, false)]), "sv.print.queues");
        // 這一句是本項存在的理由。
        Assert.Contains("沒有任何實體輸出裝置", f.Value);
        Assert.Contains("「有印表機」與「能印東西」在這裡是兩件事", f.Value);
        Assert.Equal(0d, f.NumericValue);
    }

    [Fact]
    public void 列印_離線是當下狀態不是故障()
    {
        var f = Fact(ServerRoleFilePrintFactsService.Collect(At, printers: () =>
            [new(true, false, true)]), "sv.print.queues");
        Assert.Contains("離線 1 台", f.Value);
        Assert.Contains("「離線」是當下狀態不是故障", f.Value);
    }

    [Fact]
    public void 列印_零台時說明服務與裝置是兩件事()
    {
        var f = Fact(ServerRoleFilePrintFactsService.Collect(At, printers: () => []), "sv.print.queues");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(0d, f.NumericValue);
        Assert.Contains("服務在跑但沒有裝置", f.Value);
    }

    [Fact]
    public void 列印_讀取失敗是ReadError()
    {
        var f = Fact(ServerRoleFilePrintFactsService.Collect(At, printers: () => null), "sv.print.queues");
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── 契約 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 兩把鍵齊且互不影響()
    {
        var facts = ServerRoleFilePrintFactsService.Collect(At,
            shares: () => throw new InvalidOperationException("s"),
            printers: () => [new(true, false, false)]);
        Assert.Equal(new[] { "sv.file.shares", "sv.print.queues" },
            facts.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(FactAvailability.ReadError, Fact(facts, "sv.file.shares").Availability);
        Assert.Equal(FactAvailability.Present, Fact(facts, "sv.print.queues").Availability);
    }
}
