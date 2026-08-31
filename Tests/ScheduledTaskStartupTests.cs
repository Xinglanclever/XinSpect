using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 排程工作啟動項的解析。要守住的是三件事：認出登入／開機觸發、只收這兩種觸發、
/// 以及壞掉的工作定義不能拖垮整份清單。
/// </summary>
public class ScheduledTaskStartupTests
{
    /// <summary>真實工作定義的骨架（含命名空間，和 System32\Tasks 底下的檔案一致）。</summary>
    private static string Xml(string triggers, string actions = ExecAction, string settingsEnabled = "true")
        => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Author>廠商</Author></RegistrationInfo>
          <Triggers>{triggers}</Triggers>
          <Settings><Enabled>{settingsEnabled}</Enabled></Settings>
          <Actions Context="Author">{actions}</Actions>
        </Task>
        """;

    private const string ExecAction =
        "<Exec><Command>C:\\Program Files\\Foo\\foo.exe</Command><Arguments>/silent</Arguments></Exec>";

    // ── 認出開機自啟 ──────────────────────────────────────────────────────

    [Fact]
    public void 登入觸發認得出來並帶出指令()
    {
        var info = ScheduledTaskStartup.Parse(Xml("<LogonTrigger><Enabled>true</Enabled></LogonTrigger>"), @"\FooUpdater");
        Assert.NotNull(info);
        Assert.Equal("登入時", info.TriggerText);
        Assert.Equal(@"C:\Program Files\Foo\foo.exe /silent", info.Command);
        Assert.Equal("FooUpdater", info.Name);
        Assert.True(info.Enabled);
        Assert.False(info.SystemBuiltIn);
    }

    [Fact]
    public void 開機觸發認得出來()
    {
        var info = ScheduledTaskStartup.Parse(Xml("<BootTrigger/>"), @"\Boot\Thing");
        Assert.NotNull(info);
        Assert.Equal("開機時", info.TriggerText);
        Assert.Equal("Thing", info.Name);     // 只取最後一段
    }

    [Fact]
    public void 兩種觸發都有就都說()
    {
        var info = ScheduledTaskStartup.Parse(Xml("<LogonTrigger/><BootTrigger/>"), @"\Both");
        Assert.Equal("登入時＋開機時", info!.TriggerText);
    }

    [Fact]
    public void 觸發程序自己被關掉時要講出來()
    {
        // 工作是啟用的，但唯一的觸發程序被停了——不講清楚，「啟用中」會看起來自相矛盾
        var info = ScheduledTaskStartup.Parse(
            Xml("<LogonTrigger><Enabled>false</Enabled></LogonTrigger>"), @"\Quiet");
        Assert.NotNull(info);
        Assert.Contains("觸發程序已停用", info.TriggerText);
        Assert.True(info.Enabled);
    }

    // ── 只收開機自啟 ──────────────────────────────────────────────────────

    [Fact]
    public void 定時或閒置觸發不屬於開機自啟()
    {
        Assert.Null(ScheduledTaskStartup.Parse(
            Xml("<CalendarTrigger><StartBoundary>2026-01-01T03:00:00</StartBoundary></CalendarTrigger>"), @"\Daily"));
        Assert.Null(ScheduledTaskStartup.Parse(Xml("<IdleTrigger/>"), @"\Idle"));
    }

    [Fact]
    public void 沒有觸發程序區塊就不是啟動項()
    {
        const string noTriggers = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions><Exec><Command>x.exe</Command></Exec></Actions>
            </Task>
            """;
        Assert.Null(ScheduledTaskStartup.Parse(noTriggers, @"\NoTrigger"));
    }

    // ── 壞資料不能拖垮清單 ────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("這不是 XML")]
    [InlineData("<Task><Triggers><LogonTrigger/>")]   // 截斷的檔案
    public void 壞掉的工作定義安靜略過(string? xml)
    {
        Assert.Null(ScheduledTaskStartup.Parse(xml, @"\Broken"));
    }

    [Fact]
    public void 沒有命名空間也要解析得出來()
    {
        // 工作定義的 schema 從 1.0 到 1.6 都在流通，綁死命名空間會在某些機器上整批漏掉
        Assert.NotNull(ScheduledTaskStartup.Parse(
            "<Task><Triggers><LogonTrigger/></Triggers><Actions><Exec><Command>a.exe</Command></Exec></Actions></Task>",
            @"\NoNs"));
    }

    // ── 啟用狀態與動作 ────────────────────────────────────────────────────

    [Fact]
    public void 工作被停用時如實顯示()
    {
        var info = ScheduledTaskStartup.Parse(Xml("<LogonTrigger/>", settingsEnabled: "false"), @"\Off");
        Assert.False(info!.Enabled);
    }

    [Fact]
    public void 沒有Settings區塊時視為啟用()
    {
        var info = ScheduledTaskStartup.Parse(
            "<Task><Triggers><LogonTrigger/></Triggers><Actions><Exec><Command>a.exe</Command></Exec></Actions></Task>",
            @"\NoSettings");
        Assert.True(info!.Enabled);
    }

    [Fact]
    public void 只有COM處理常式時如實寫出CLSID()
    {
        var info = ScheduledTaskStartup.Parse(
            Xml("<LogonTrigger/>", "<ComHandler><ClassId>{1936ED8A-BD93-4000-9E00-000000000000}</ClassId></ComHandler>"),
            @"\Com");
        Assert.Contains("COM 處理常式", info!.Command);
        Assert.Contains("1936ED8A", info.Command);
    }

    [Fact]
    public void 沒有可執行動作時承認沒有()
    {
        var info = ScheduledTaskStartup.Parse(Xml("<LogonTrigger/>", "<ShowMessage><Title>嗨</Title></ShowMessage>"), @"\Msg");
        Assert.Contains("沒有可執行的動作", info!.Command);
    }

    [Theory]
    [InlineData("a.exe", "/x", "a.exe /x")]
    [InlineData("a.exe", "", "a.exe")]
    [InlineData("a.exe", null, "a.exe")]
    [InlineData("", "", "")]
    public void 指令與參數併成一行(string cmd, string? args, string expected)
    {
        Assert.Equal(expected, ScheduledTaskStartup.CommandText(cmd, args));
    }

    // ── Windows 內建 ──────────────────────────────────────────────────────

    [Fact]
    public void 微軟底下的工作標為系統內建()
    {
        Assert.True(ScheduledTaskStartup.IsSystemBuiltIn(@"\Microsoft\Windows\UpdateOrchestrator\Reboot"));
        Assert.False(ScheduledTaskStartup.IsSystemBuiltIn(@"\GoogleUpdateTaskMachineUA"));
        Assert.False(ScheduledTaskStartup.IsSystemBuiltIn(@"\MicrosoftEdgeUpdateTaskMachineUA"));  // 前綴要完整比對
        Assert.False(ScheduledTaskStartup.IsSystemBuiltIn(""));
    }
}
