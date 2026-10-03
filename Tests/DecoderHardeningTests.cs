using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 突變測試逃逸補齊（ITER43）：Stryker 解鎖後的首輪報告顯示
/// CpuGeneration／MegaRaid／Ipmi／Tpm2／AudioFormat／SpecRef 的個別分支與查表項
/// 有未被釘死的逃逸突變——本檔逐臂釘值（手算向量或完整查表），讓每個 switch 臂
/// 與每個防越界分支都有斷言守著。知識表（SuperIoKnowledge／PciKnowledge）屬資料檔，
/// 逐條字串斷言＝快照重複，刻意排除在 mutate 範圍外（stryker-config.json 有註明）。
/// </summary>
public class DecoderHardeningTests
{
    // ===== CpuGeneration：DecodeSignature 完整數學＋GenerationName 全表 =====

    [Fact]
    public void CPUID簽章解碼_擴充家族與擴充模型進位()
    {
        // 7980XE（Skylake-X）：family 6、model 0x55、stepping 5 → EAX = 0x050655
        var (f, m, s) = CpuGeneration.DecodeSignature(0x0005_0655);
        Assert.Equal(6, f);
        Assert.Equal(0x55, m);
        Assert.Equal(5, s);

        // 擴充 model 進位：extModel=0xA、低 4 位 0x5 → model 0xA5（Comet Lake）
        (f, m, s) = CpuGeneration.DecodeSignature(0x000A_0653);
        Assert.Equal(0xA5, m);
        Assert.Equal(3, s);

        // 擴充 family 進位：base family=0xF 時 family=base+extended（SDM 規則）——0x0F+0x0F=0x1E
        (f, m, s) = CpuGeneration.DecodeSignature(0x00F0_0F33);
        Assert.Equal(0x1E, f);
        Assert.Equal(0x03, m);
        Assert.Equal(0x03, s);

        // base family 非 0xF 時不加 extended（本機 7980XE 的 family 6 不受高位位元污染）
        (f, m, s) = CpuGeneration.DecodeSignature(0x00F0_0655);
        Assert.Equal(6, f);
    }

    [Fact]
    public void 世代對照表_全收錄項逐條釘值()
    {
        Assert.Equal("Skylake（伺服器 SP）", CpuGeneration.GenerationName(6, 0x4F));
        Assert.Equal("Skylake-X / Cascade Lake（HEDT／伺服器）", CpuGeneration.GenerationName(6, 0x55));
        Assert.Equal("Skylake（用戶端）", CpuGeneration.GenerationName(6, 0x5E));
        Assert.Contains("Kaby Lake", CpuGeneration.GenerationName(6, 0x8E));
        Assert.Equal("Kaby Lake-X / Coffee Lake（用戶端）", CpuGeneration.GenerationName(6, 0x9E));
        Assert.Equal("Comet Lake（用戶端）", CpuGeneration.GenerationName(6, 0xA5));
        Assert.Equal("Comet Lake（行動）", CpuGeneration.GenerationName(6, 0xA6));
        Assert.Equal("Rocket Lake（用戶端）", CpuGeneration.GenerationName(6, 0x97));
        Assert.Equal("Alder Lake（用戶端）", CpuGeneration.GenerationName(6, 0x9A));
        Assert.Equal("Raptor Lake（用戶端）", CpuGeneration.GenerationName(6, 0xB7));
        Assert.Equal("Raptor Lake-P（行動）", CpuGeneration.GenerationName(6, 0xBA));
        Assert.Equal("Meteor Lake（用戶端）", CpuGeneration.GenerationName(6, 0xAA));
    }

    [Fact]
    public void 世代對照表_非家族6與未收錄model一律不猜()
    {
        Assert.Null(CpuGeneration.GenerationName(6, 0x50));
        Assert.Null(CpuGeneration.GenerationName(0xF, 0x55));
        Assert.Null(CpuGeneration.GenerationName(6, 0x00));
    }

    // ===== SpecRef：建構子契約＋註冊表內容釘死 =====

    [Fact]
    public void SpecRef建構子_空引用拒收_內容原樣承載()
    {
        Assert.Throws<ArgumentException>(() => new SpecRefAttribute(""));
        Assert.Throws<ArgumentException>(() => new SpecRefAttribute("   "));
        var a = new SpecRefAttribute("Intel SDM Vol.2, CPUID");
        Assert.Equal("Intel SDM Vol.2, CPUID", a.Reference);
    }

    [Fact]
    public void SpecRef註冊表_受檢解碼器逐條在案()
    {
        var expected = new[]
        {
            "SpiFlash", "ChipsetSecurity", "PlatformSecurity", "PcieAer", "AcpiTable", "Cmos",
            "Tsod", "SuperIo", "PlatformTrustDecoder", "PciKnowledge", "PciBars", "SuperIoKnowledge",
            "WifiBssDecoder", "MonitorConnectionDecoder",
        };
        Assert.Equal(expected, SpecRefRegistry.CoveredDecoders.Select(t => t.Name).ToArray());
        Assert.All(SpecRefRegistry.CoveredDecoders, t => Assert.True(t.IsSealed || t.IsClass));
    }

    [Fact]
    public void SpecRef覆蓋檢查_無缺引用_全部引用非空且可列舉()
    {
        Assert.Empty(SpecRefRegistry.MethodsMissingRefs());
        var refs = SpecRefRegistry.AllReferences();
        Assert.NotEmpty(refs);
        Assert.All(refs, r => Assert.False(string.IsNullOrWhiteSpace(r.Reference)));
    }

    // ===== MegaRAID：命令碼全表＋Flags 欄位＋Redfish 結構面 =====

    [Fact]
    public void MFI命令碼全表逐條釘值()
    {
        Assert.Equal("INIT", MegaRaidDecoder.CommandName(0x00));
        Assert.Equal("LD_READ", MegaRaidDecoder.CommandName(0x01));
        Assert.Equal("LD_WRITE", MegaRaidDecoder.CommandName(0x02));
        Assert.Equal("LD_SCSI_IO", MegaRaidDecoder.CommandName(0x03));
        Assert.Equal("ABORT", MegaRaidDecoder.CommandName(0x06));
        Assert.Equal("Command 0x07（未收錄）", MegaRaidDecoder.CommandName(0x07));
    }

    [Fact]
    public void MFI訊框_Flags與SenseLength逐欄位承載()
    {
        var frame = new byte[] { 0x02, 0x11, 0x01, 0x80, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00 };
        var f = MegaRaidDecoder.DecodeMfiFrame(frame);
        Assert.NotNull(f);
        Assert.Equal(0x11, f!.SenseLength);
        Assert.Equal(0x80, f.Flags);
        Assert.Equal(0x40u, f.DataTransferLength);
    }

    [Fact]
    public void Redfish包封_類型與Members陣列推數_根非物件拒解()
    {
        var env = RedfishSchemaDecoder.DecodeEnvelope("""
            { "@odata.id": "/redfish/v1", "@odata.type": "#ServiceRoot.v1_0_0" }
            """);
        Assert.NotNull(env);
        Assert.Equal("#ServiceRoot.v1_0_0", env!.OdataType);
        Assert.Null(env.MemberCount);

        var byArray = RedfishSchemaDecoder.DecodeEnvelope("""
            { "Members": [ {"@odata.id": "1"}, {"@odata.id": "2"}, {"@odata.id": "3"} ] }
            """);
        Assert.NotNull(byArray);
        Assert.Equal(3, byArray!.MemberCount);

        Assert.Null(RedfishSchemaDecoder.DecodeEnvelope("[1, 2, 3]")); // 根是陣列不是物件
    }

    [Fact]
    public void Redfish錯誤_無message走ExtendedInfo第一條()
    {
        var ext = RedfishSchemaDecoder.DecodeEnvelope("""
            { "error": { "@Message.ExtendedInfo": [ { "MessageId": "Base.1.0.Success", "Message": "延伸訊息" } ] } }
            """);
        Assert.NotNull(ext);
        Assert.Contains("延伸訊息", ext!.ErrorBrief);

        var emptyExt = RedfishSchemaDecoder.DecodeEnvelope(
            """{ "error": { "@Message.ExtendedInfo": [] } }""");
        Assert.NotNull(emptyExt);
        Assert.Null(emptyExt!.ErrorBrief);
    }

    // ===== IPMI：Sensor/Event 型別全表＋方向位元＋FRU 邊界分支 =====

    [Fact]
    public void IPMI感測器型別全表逐條釘值()
    {
        Assert.Equal("溫度", IpmiDecoder.SensorTypeName(0x01));
        Assert.Equal("電壓", IpmiDecoder.SensorTypeName(0x02));
        Assert.Equal("電流", IpmiDecoder.SensorTypeName(0x03));
        Assert.Equal("風扇", IpmiDecoder.SensorTypeName(0x04));
        Assert.Equal("處理器", IpmiDecoder.SensorTypeName(0x07));
        Assert.Equal("電源供應器", IpmiDecoder.SensorTypeName(0x08));
        Assert.Equal("記憶體", IpmiDecoder.SensorTypeName(0x0C));
        Assert.Equal("系統板", IpmiDecoder.SensorTypeName(0x0F));
        Assert.Equal("電池", IpmiDecoder.SensorTypeName(0x21));
        Assert.Equal("Sensor Type 0xFF（未收錄）", IpmiDecoder.SensorTypeName(0xFF));
    }

    [Fact]
    public void IPMI事件型別全表逐條釘值()
    {
        Assert.Equal("未指定", IpmiDecoder.EventTypeName(0x00));
        Assert.Equal("門檻：轉入 Lower Non-critical", IpmiDecoder.EventTypeName(0x01));
        Assert.Equal("門檻：轉入 Lower Critical", IpmiDecoder.EventTypeName(0x02));
        Assert.Equal("門檻：轉入 Lower Non-recoverable", IpmiDecoder.EventTypeName(0x03));
        Assert.Equal("門檻：轉入 Upper Non-critical", IpmiDecoder.EventTypeName(0x04));
        Assert.Equal("門檻：轉入 Upper Critical", IpmiDecoder.EventTypeName(0x05));
        Assert.Equal("門檻：轉入 Upper Non-recoverable", IpmiDecoder.EventTypeName(0x06));
        Assert.Equal("Sensor-specific 離散事件", IpmiDecoder.EventTypeName(0x6F));
        Assert.Equal("Event Type 0x70（未收錄）", IpmiDecoder.EventTypeName(0x70));
    }

    [Fact]
    public void SEL事件_deassertion方向與OEM位元組界()
    {
        var record = new byte[16];
        record[2] = 0x02;
        record[10] = 0x02;                       // 電壓
        record[12] = 0x81;                       // bit7=1 deassertion、type 0x01
        record[13] = 0x3F;                       // ED1 bits[7:6]=00 → 非 OEM
        var (ev, error) = IpmiDecoder.DecodeSelEvent(record);
        Assert.Null(error);
        Assert.NotNull(ev);
        Assert.False(ev!.Assertion);
        Assert.Equal("門檻：轉入 Lower Non-critical", ev.EventTypeName);
        Assert.Equal("電壓", ev.SensorTypeName);
        Assert.False(ev.OemEventData);
    }

    [Fact]
    public void FRU板卡區_區長過短或截斷或非ASCII如實拒解()
    {
        // Board 偏移 1（×8=8）但長度單位 0 → 無效
        var zeroLen = new byte[16];
        zeroLen[3] = 0x01;
        Assert.Null(IpmiDecoder.DecodeFruBoardArea(zeroLen));

        // 區長宣告超出緩衝 → 拒解
        var overrun = new byte[16];
        overrun[3] = 0x01;
        overrun[8 + 1] = 0x40;                   // 64 位元組 > 剩餘
        Assert.Null(IpmiDecoder.DecodeFruBoardArea(overrun));

        // 宣告在界內但字串 TLV 是 6-bit packed（bits[7:6]=00）→ 如實回空字串
        var sixBit = new byte[32];
        sixBit[3] = 0x01;
        sixBit[9] = 0x02;                        // 區長 16 bytes
        sixBit[8 + 6] = 0x04;                    // TL：6-bit packed，不解
        var board = IpmiDecoder.DecodeFruBoardArea(sixBit);
        Assert.NotNull(board);
        Assert.Equal("", board!.Manufacturer);
        Assert.Null(board.ManufacturingDate);    // 分鐘數 0＝未指定
    }

    // ===== TPM2：演算法全表＋回應邊界＋log 走訪損毀分支 =====

    [Fact]
    public void TPM摘要長度_全演算法逐條()
    {
        Assert.Equal(20, Tpm2.DigestSize(Tpm2.AlgSha1));
        Assert.Equal(32, Tpm2.DigestSize(Tpm2.AlgSha256));
        Assert.Equal(48, Tpm2.DigestSize(Tpm2.AlgSha384));
        Assert.Equal(64, Tpm2.DigestSize(Tpm2.AlgSha512));
        Assert.Equal(32, Tpm2.DigestSize(Tpm2.AlgSm3));
        Assert.Equal(0, Tpm2.DigestSize(0x0000));
    }

    [Fact]
    public void TPM回應_size為零或越界拒解_參數大小欄不可信()
    {
        // size 欄＝0
        var zeroSize = new byte[12];
        Assert.Null(Tpm2.DecodePcrResponse(zeroSize, out uint rc0));
        Assert.Equal(0u, rc0);

        // size 宣告超出剩餘長度
        var overrun = new byte[12];
        overrun[10] = 0x00; overrun[11] = 0x20;  // size=32 但沒有 payload
        Assert.Null(Tpm2.DecodePcrResponse(overrun, out _));

        // 過短
        Assert.Null(Tpm2.DecodePcrResponse(new byte[11], out _));
    }

    [Fact]
    public void TCGlog_count上限外即停_越界即停_尾隨垃圾標truncated()
    {
        // count=17（超過合理上限 16）→ 立即停（u32 大端序：低位元組在 offset 11）
        var badCount = new byte[12];
        badCount[11] = 0x11;
        var w1 = Tpm2.WalkTcgLog(badCount);
        Assert.Empty(w1.Events);
        Assert.True(w1.Truncated);

        // 摘要宣告長度越界 → 停在第一事件前
        var digestOverrun = new byte[14];
        digestOverrun[11] = 0x01;                // count=1
        digestOverrun[12] = 0x00; digestOverrun[13] = 0x0B; // SHA-256 但沒有 32 bytes
        var w2 = Tpm2.WalkTcgLog(digestOverrun);
        Assert.Empty(w2.Events);
        Assert.True(w2.Truncated);

        // 完整事件（12 標頭＋2+32 摘要＋4 事件大小＋4 事件內容＝54 bytes）後尾隨垃圾 → truncated
        var tail = new byte[57];
        tail[11] = 0x01;                         // count=1
        tail[12] = 0x00; tail[13] = 0x0B;        // SHA-256
        tail[46] = 0x00; tail[49] = 0x04;        // 事件大小 4（u32 大端序：低位元組在 offset 49）
        var w3 = Tpm2.WalkTcgLog(tail);
        Assert.Single(w3.Events);                // 事件本身完整解出
        Assert.True(w3.Truncated);               // 但尾端 3 bytes 來路不明

        // 恰好用完 → 完整（Truncated=false）
        var exact = new byte[54];
        exact[11] = 0x01;
        exact[12] = 0x00; exact[13] = 0x0B;
        exact[49] = 0x04;
        var w4 = Tpm2.WalkTcgLog(exact);
        Assert.Single(w4.Events);
        Assert.False(w4.Truncated);
    }

    // ===== AudioFormat：通道／取樣率上界＋EXTENSIBLE 短宣告＋未知 tag =====

    [Theory]
    [InlineData(65, 48000u)]     // 通道數上限 64
    [InlineData(2, 2_000_001u)]  // 取樣率上限 2 MHz
    public void WaveFormat_通道或取樣率超出上界拒解(ushort channels, uint rate)
    {
        var data = new byte[16];
        data[0] = 0x01;                          // PCM
        data[2] = (byte)channels; data[3] = (byte)(channels >> 8);
        data[4] = (byte)rate; data[5] = (byte)(rate >> 8); data[6] = (byte)(rate >> 16); data[7] = (byte)(rate >> 24);
        Assert.Null(AudioFormatDecoder.Parse(data));
    }

    [Fact]
    public void WaveFormat_EXTENSIBLE不足40位元組如實標_未知tag帶原始碼()
    {
        var shortExt = new byte[16];
        shortExt[0] = 0xFE; shortExt[1] = 0xFF;
        shortExt[2] = 2; shortExt[4] = 0x50; shortExt[5] = 0xBB; // 48000
        shortExt[14] = 24;
        var f = AudioFormatDecoder.Parse(shortExt);
        Assert.NotNull(f);
        Assert.Contains("不足 40", f!.Value.TagText);

        var unknown = new byte[16];
        unknown[0] = 0x11; unknown[1] = 0x00;
        unknown[2] = 2; unknown[4] = 0x80; unknown[5] = 0x3E;   // 16000
        unknown[14] = 16;
        var u = AudioFormatDecoder.Parse(unknown);
        Assert.NotNull(u);
        Assert.Equal("formatTag 0x0011", u!.Value.TagText);

        Assert.Equal("2 聲道、16000 Hz、16-bit（formatTag 0x0011）", AudioFormatDecoder.Describe(u!.Value));
    }
}
