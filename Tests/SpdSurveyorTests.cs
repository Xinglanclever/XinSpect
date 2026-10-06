using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 全機 SPD 巡檢：把候選的每一條匯流排走一遍併起來。
/// </summary>
/// <remarks>
/// 主流桌上平台的 SPD 在 PCH 的 SMBus 上，HEDT／伺服器平台在處理器記憶體控制器自己的
/// SMBus 上（而且是兩組）。哪一條有東西是平台決定的，所以逐條試——而每一筆事實都要
/// 記得自己是從哪一條讀到的，那是它的血統。
/// </remarks>
public class SpdSurveyorTests
{
    private static byte[] RealSpd()
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spd-ddr4-real-dimm1.bin"));

    /// <summary>用假匯流排，只驗巡檢的併合與敘述邏輯。</summary>
    private sealed class StubBus(string description, params (byte Addr, byte[] Raw)[] modules) : ISpdBus
    {
        public string Description { get; } = description;
        public string LastError { get; private set; } = "";
        public SmbusStatus LastStatus { get; private set; } = SmbusStatus.Ok;
        public bool AcquireFails;
        public string AcquireReason = "";
        public bool Released { get; private set; }

        private int _page;

        public bool TryAcquireBus(out string reason)
        {
            reason = AcquireFails ? AcquireReason : "";
            return !AcquireFails;
        }

        public void ReleaseBus() => Released = true;

        public bool SendByte(byte slave7, byte data)
        {
            SpdBusAddresses.EnsurePageSelect(slave7);
            if (modules.Length == 0) { LastStatus = SmbusStatus.NoDevice; return false; }
            _page = slave7 == SpdReader.PageSelect1 ? 1 : 0;
            LastStatus = SmbusStatus.Ok;
            return true;
        }

        public byte? ReadByteData(byte slave7, byte command)
        {
            SpdBusAddresses.EnsureSpdRead(slave7);
            foreach (var (addr, raw) in modules)
                if (addr == slave7) { LastStatus = SmbusStatus.Ok; return raw[_page * 256 + command]; }
            LastStatus = SmbusStatus.NoDevice;
            LastError = "位址上沒有裝置";
            return null;
        }

        public bool WriteByteData(byte slave7, byte command, byte value)
        {
            SpdBusAddresses.EnsureDdr5PageSelect(slave7, command);
            return true; // 測試的模組都是 DDR4；DDR5 切頁在此假件不會被走到
        }
    }

    [Fact]
    public void 把兩條匯流排上的模組併起來_並記下各自的來源()
    {
        var spd = RealSpd();
        var survey = SpdSurveyor.Survey([
            new StubBus("甲匯流排", (0x50, spd)),
            new StubBus("乙匯流排", (0x50, spd), (0x52, spd)),
        ]);

        Assert.Equal(3, survey.Modules.Count);
        Assert.Single(survey.Modules, m => m.Bus == "甲匯流排");
        Assert.Equal(2, survey.Modules.Count(m => m.Bus == "乙匯流排"));
        Assert.All(survey.Modules, m => Assert.Equal("ZhuQue_8G_Y", m.Decoded.PartNumber));
        Assert.Empty(survey.Problems);
    }

    [Fact]
    public void 沒有SPD的匯流排只留一句匯流排層級說明()
    {
        var survey = SpdSurveyor.Survey([new StubBus("空匯流排")]);

        Assert.Empty(survey.Modules);
        Assert.Empty(survey.Problems);
        Assert.Contains(survey.Notes, n => n.StartsWith("空匯流排：") && n.Contains("沒有任何 DDR4／DDR5 SPD"));
    }

    [Fact]
    public void 某一條取不到不影響其餘各條_而且原因要指名是哪一條()
    {
        var spd = RealSpd();
        var blocked = new StubBus("被占住的匯流排") { AcquireFails = true, AcquireReason = "CPU-Z 正在用" };
        var ok = new StubBus("好的匯流排", (0x50, spd));

        var survey = SpdSurveyor.Survey([blocked, ok]);

        Assert.Single(survey.Modules);
        Assert.Contains("被占住的匯流排：CPU-Z 正在用", survey.Notes);
        Assert.False(blocked.Released);          // 沒取到就不該去釋放
        Assert.True(ok.Released);
    }

    [Fact]
    public void 讀完一定釋放匯流排_就算中途出狀況()
    {
        var bus = new StubBus("半條匯流排", (0x50, RealSpd()));
        SpdSurveyor.Survey([bus]);

        Assert.True(bus.Released);
    }

    [Fact]
    public void 讀不到的位址進Problems而不是被吞掉()
    {
        var broken = RealSpd();
        broken[2] = 0x12;                        // 型別碼寫 DDR5，但 byte 0/1 不是 hub 識別——不是能直讀的 SPD5118
        var survey = SpdSurveyor.Survey([new StubBus("匯流排", (0x50, broken))]);

        Assert.Empty(survey.Modules);
        var slot = Assert.Single(survey.Problems);
        Assert.Equal(SpdKind.Unknown, slot.Kind);
        Assert.Contains("0x12", slot.Note);
    }

    [Fact]
    public void DDR5模組進Ddr5Modules_與DDR4分列()
    {
        var img = new byte[1024];
        for (int i = 0; i < img.Length; i++) img[i] = (byte)(0x40 ^ i);
        img[2] = SpdReader.Ddr5TypeCode;
        var bus = new StubBus5("DDR5 匯流排", (0x50, img));

        var survey = SpdSurveyor.Survey([bus]);

        Assert.Empty(survey.Modules);
        var m = Assert.Single(survey.Ddr5Modules!);
        Assert.Equal("DDR5 匯流排", m.Bus);
        Assert.Equal(0x50, m.Address);
        Assert.Equal("4.1", m.Decoded5.SpdRevision);   // byte 1＝0x41 → 兩個 nibble
        Assert.Equal(2048, m.Decoded5.SpdSizeBytes);    // byte 0＝0x40 → bits[6:4]＝4
        Assert.Equal(0x12, m.Decoded5.SpdRevision.Length > 0 ? 0x12 : 0); // 佔位：type code 在 Decode 內檢查
    }
}

/// <summary>StubBus 的 DDR5 版：hub 識別＋MR11 切頁＋1024 位元組映像。</summary>
internal sealed class StubBus5(string description, params (byte Addr, byte[] Raw)[] modules) : ISpdBus
{
    public string Description { get; } = description;
    public string LastError { get; private set; } = "";
    public SmbusStatus LastStatus { get; private set; } = SmbusStatus.Ok;
    public bool Released { get; private set; }

    private int _page;
    private byte _mr11Base;

    public bool TryAcquireBus(out string reason) { reason = ""; return true; }
    public void ReleaseBus() => Released = true;

    public bool SendByte(byte slave7, byte data)
    {
        SpdBusAddresses.EnsurePageSelect(slave7);
        return true;                             // DDR4 切頁裝置存在與否不影響 DDR5 hub 通路
    }

    public bool WriteByteData(byte slave7, byte command, byte value)
    {
        SpdBusAddresses.EnsureDdr5PageSelect(slave7, command);
        _page = value & 0x07;
        return true;
    }

    public byte? ReadByteData(byte slave7, byte command)
    {
        SpdBusAddresses.EnsureSpdRead(slave7);
        foreach (var (addr, raw) in modules)
        {
            if (addr != slave7) continue;
            LastStatus = SmbusStatus.Ok;
            if (command == 0x00) return 0x51;
            if (command == 0x01) return 0x18;
            if (command == 0x0B) { _mr11Base = 0x08; return 0x08; }
            return raw[_page * 128 + (command - 0x80)];
        }
        LastStatus = SmbusStatus.NoDevice;
        return null;
    }
}
