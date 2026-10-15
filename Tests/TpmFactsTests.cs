using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// TPM 量測開機鏈事實（WP14）的契約：注入 TBS 假通道驗證 PCR 0–7 逐顆成列與 TCG log 摘要、
/// 無 TPM 三態、TBS/TPM 錯誤如實帶碼。不碰真實 TBS。
/// </summary>
public class TpmFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    private static byte[] PcrResponse(uint rc, byte[] digest)
    {
        var r = new byte[12 + (rc == 0 ? 2 + digest.Length : 0)];
        r[0] = 0x80; r[1] = 0x01;
        PutBE(r, 2, (uint)r.Length);
        PutBE(r, 6, rc);
        if (rc == 0) { PutBE16(r, 10, (ushort)digest.Length); digest.CopyTo(r, 12); }
        return r;
    }

    private static void PutBE(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16); b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
    }

    private static void PutBE16(byte[] b, int off, ushort v) { b[off] = (byte)(v >> 8); b[off + 1] = (byte)v; }

    private static byte[] MakeTcgLog() => Tpm2.WalkTcgLog(MakeTcgLogRaw()).Events.Count >= 1 ? MakeTcgLogRaw() : [];

    private static byte[] MakeTcgLogRaw()
    {
        var e1 = new byte[4 + 4 + 4 + 2 + 32 + 4 + 3];
        PutBE(e1, 0, 0); PutBE(e1, 4, 0x80000007u); PutBE(e1, 8, 1); PutBE16(e1, 12, 0x000B);
        for (int i = 0; i < 32; i++) e1[14 + i] = (byte)i;
        PutBE(e1, 46, 3);
        var e2 = new byte[4 + 4 + 4 + 2 + 20 + 4 + 1];
        PutBE(e2, 0, 7); PutBE(e2, 4, 0x80000007u); PutBE(e2, 8, 1); PutBE16(e2, 12, 0x0004);
        for (int i = 0; i < 20; i++) e2[14 + i] = (byte)(i + 5);
        PutBE(e2, 34, 1);
        return e1.Concat(e2).ToArray();
    }

    /// <summary>
    /// 假通道的預設命令回應。既有測試只認 PCR Read（20 位元組）；P2-063／P2-068 之後服務多問兩次
    /// GetCapability（22 位元組），這裡照 TPM 2.0 Part 3 的格式回一份最小可用回應，
    /// 讓既有斷言維持「這個通道只驗 PCR 與 log」的原意，不被新鍵干擾。
    /// </summary>
    private static (uint, byte[], uint) ChannelResponse(byte[] cmd, byte[] pcrDigest, uint pcrLen)
        => cmd.Length == 20
            ? (0u, PcrResponse(0, pcrDigest), pcrLen)
            : (0u, GetCapabilityResponse(cmd), (uint)GetCapabilityResponse(cmd).Length);

    /// <summary>依命令裡的 capability 欄位（位移 10）回對應的 capabilityData。</summary>
    private static byte[] GetCapabilityResponse(byte[] cmd)
    {
        uint capability = ((uint)cmd[10] << 24) | ((uint)cmd[11] << 16) | ((uint)cmd[12] << 8) | cmd[13];
        var data = capability == Tpm2.CapAlgorithms ? AlgPropertyData() : TpmPropertyData();
        int paramSize = 19 + data.Length;
        var r = new byte[paramSize];
        r[0] = 0x80; r[1] = 0x01;
        PutBE(r, 2, (uint)paramSize);
        PutBE(r, 6, 0);
        r[10] = 0;                    // moreData
        PutBE(r, 11, capability);
        data.CopyTo(r, 19);           // count u32 ＋ 條目（moreData 1 ＋ capability 4 之後）
        return r;
    }

    /// <summary>TPML_ALG_PROPERTY：SHA-1／SHA-256／SHA-384 皆為雜湊（TPMA_ALGORITHM bit2）。</summary>
    private static byte[] AlgPropertyData() => [0, 0, 0, 3,
        0x00, 0x04, 0x00, 0x00, 0x00, 0x04,
        0x00, 0x0B, 0x00, 0x00, 0x00, 0x04,
        0x00, 0x0C, 0x00, 0x00, 0x00, 0x04];

    /// <summary>TPML_TAGGED_TPM_PROPERTY：family indicator '2.0\0' 與 revision 1.3。
    /// revision 依本專案在 <c>Tpm2.Revision</c> 的取法：主版號在 bits 31:24、次版號在 bits 15:8
    /// （bits 23:16 保留），所以 1.3 是 <c>0x01000300</c>。
    /// 對照：1.0 是 <c>0x01000000</c>、1.16 是 <c>0x01001000</c>——
    /// <b>不是</b>把 major/minor 直接拼成 0x01 03 00 00（那是 1.0）。
    /// 這條與 Tpm2.Revision 的取法（及其規格出處註記）是同一份定義，fixture 寫錯會讓正確的實作看起來是錯的。</summary>
    private static byte[] TpmPropertyData() => [0, 0, 0, 2,
        0x00, 0x00, 0x01, 0x00, (byte)'2', (byte)'.', (byte)'0', 0x00,
        0x00, 0x00, 0x01, 0x01, 0x01, 0x00, 0x03, 0x00];

    /// <summary>TPML_TAGGED_TPM_PROPERTY：兩個屬性都缺席（空屬性集）。
    /// 用來驗「TPM 沒回報 family 與 revision 時不以 2.0 代填」——count 為 0，
    /// 解出來是空清單，服務必須走「沒有值就是沒有值」那條路。</summary>
    private static byte[] EmptyPropertyData() => [0, 0, 0, 0];

    [Fact]
    public void 全鏈成功_PCR0到7逐顆成列_TCGlog摘要帶事件數()
    {
        var digest = Enumerable.Range(0, 32).Select(i => (byte)(i ^ 0x5A)).ToArray();
        var facts = TpmFactsService.Collect(At,
            createContext: () => (nint)0x1234,
            submit: (_, cmd) => ChannelResponse(cmd, digest, 12 + 2 + 32),
            readLog: _ => MakeTcgLog());

        Assert.Single(facts, f => f.Key == "tpm.present");
        Assert.Contains("可用", Assert.Single(facts, f => f.Key == "tpm.present").Value);
        for (uint i = 0; i < 8; i++)
        {
            var pcr = Assert.Single(facts, f => f.Key == $"tpm.pcr.sha256.{i}");
            Assert.Equal(FactAvailability.Present, pcr.Availability);
            Assert.Equal(Convert.ToHexStringLower(digest), pcr.Value);
        }
        var log = Assert.Single(facts, f => f.Key == "tpm.tcg_log");
        Assert.Contains("2 個事件", log.Value);
        Assert.Contains("PCR 覆蓋 0、7", log.Value);
        Assert.DoesNotContain("損毀", log.Value);
    }

    [Fact]
    public void 無TPM_單一事實如實三態()
    {
        var facts = TpmFactsService.Collect(At, createContext: () => null);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("TPM 不存在", f.UnavailableReason);
    }

    [Fact]
    public void TPM拒絕與TBS錯誤_分別帶TPM_RC與TBS碼()
    {
        var facts = TpmFactsService.Collect(At,
            createContext: () => (nint)0x1234,
            submit: (_, cmd) => (0, PcrResponse(0x0000014C, []), 12),
            readLog: _ => null);

        var pcr = Assert.Single(facts, f => f.Key == "tpm.pcr.sha256.0");
        Assert.Equal(FactAvailability.NotSupported, pcr.Availability);
        Assert.Contains("TPM_RC 0x0000014C", pcr.UnavailableReason);

        var log = Assert.Single(facts, f => f.Key == "tpm.tcg_log");
        Assert.Equal(FactAvailability.NotSupported, log.Availability);
    }

    // ── 極致計畫階段二 P2-063／P2-068：銀行清單與規格版本由 TPM 自己申報 ──

    /// <summary>
    /// 銀行清單問的是 TPM「實作了哪些雜湊」，不是從 PCR 有沒有值回推。
    /// 這條同時釘住兩件事：只有雜湊屬性的條目會進清單（非對稱／對稱不算 bank），
    /// 以及服務實際嘗試讀的三個 bank 都被申報到時，值裡要這樣說。
    /// </summary>
    [Fact]
    public void 銀行清單_只列雜湊屬性且與本服務嘗試的bank對帳()
    {
        var digest = new byte[32];
        var facts = TpmFactsService.Collect(At,
            createContext: () => (nint)0x1234,
            submit: (_, cmd) => ChannelResponse(cmd, digest, 12 + 2 + 32),
            readLog: _ => null);

        var f = Assert.Single(facts, x => x.Key == "tpm.pcr.banks");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("SHA-256", f.Value);
        Assert.Contains("SHA-1", f.Value);
        Assert.Contains("SHA-384", f.Value);
        Assert.Contains("都在 TPM 申報清單內", f.Value);
        Assert.Contains("不代表任一 bank 已被量測鏈使用", f.Value);
        Assert.Equal(3, f.NumericValue);
    }

    /// <summary>TPM 沒申報某個 bank 時，要說「讀不到是這個原因」，不可以讓它看起來像讀取錯誤。</summary>
    [Fact]
    public void 銀行清單_未申報的bank要說明讀不到的原因不是錯誤()
    {
        var facts = TpmFactsService.Collect(At,
            createContext: () => (nint)0x1234,
            submit: (_, cmd) => cmd.Length == 20
                ? (0u, PcrResponse(0, new byte[32]), 12 + 2 + 32)
                // 只申報 SHA-256：本服務嘗試的 SHA-1／SHA-384 就成了未申報項
                : (0u, CapabilityWith([0, 0, 0, 1, 0x00, 0x0B, 0x00, 0x00, 0x00, 0x04]),
                       (uint)CapabilityWith([0, 0, 0, 1, 0x00, 0x0B, 0x00, 0x00, 0x00, 0x04]).Length),
            readLog: _ => null);

        var f = Assert.Single(facts, x => x.Key == "tpm.pcr.banks");
        Assert.Contains("不在 TPM 申報清單內", f.Value);
        Assert.Contains("不是讀取錯誤", f.Value);
        Assert.Equal(1, f.NumericValue);
    }

    /// <summary>回應的 capability 與查詢不符時必須標示——把別人的答案當成這個問題的答案是最糟的靜默錯誤。
    /// 這裡問 TPM_CAP_ALGORITHMS 卻回一份 TPM_CAP_TPM_PROPERTIES 的內容：服務必須看 capability 欄
    /// 發現「這不是我要的」，而不是照著解下去。長度必須與資料相符，否則會先撞到「格式不符」那條
    /// 而驗不到這條規則。</summary>
    [Fact]
    public void 銀行清單_capability不符要如實標不支援()
    {
        var r = CapabilityWith(TpmPropertyData(), capability: Tpm2.CapTpmProperties);
        var facts = TpmFactsService.Collect(At,
            createContext: () => (nint)0x1234,
            submit: (_, cmd) => cmd.Length == 20
                ? (0u, PcrResponse(0, new byte[32]), 12 + 2 + 32)
                : (0u, r, (uint)r.Length),
            readLog: _ => null);

        var f = Assert.Single(facts, x => x.Key == "tpm.pcr.banks");
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("不是查詢的", f.UnavailableReason);
    }

    /// <summary>
    /// 版本由 family indicator 與 revision 兩個屬性組成，値都要出現；
    /// 兩者都沒回報時標 NotSupported，<b>不以 2.0 代填</b>——那是本專案最忌的典型值補位。
    /// </summary>
    [Fact]
    public void 規格版本_由TPM自己的family與revision陳述()
    {
        var facts = TpmFactsService.Collect(At,
            createContext: () => (nint)0x1234,
            submit: (_, cmd) => ChannelResponse(cmd, new byte[32], 12 + 2 + 32),
            readLog: _ => null);

        var f = Assert.Single(facts, x => x.Key == "tpm.version");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("family 2.0", f.Value);
        Assert.Contains("revision 1.3", f.Value);
    }

    [Fact]
    public void 規格版本_未回報時不以預設值代填()
    {
        var facts = TpmFactsService.Collect(At,
            createContext: () => (nint)0x1234,
            submit: (_, cmd) => cmd.Length == 20
                ? (0u, PcrResponse(0, new byte[32]), 12 + 2 + 32)
                : (0u, CapabilityWith(EmptyPropertyData(), Tpm2.CapTpmProperties), 23u),
            readLog: _ => null);

        var f = Assert.Single(facts, x => x.Key == "tpm.version");
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("不以 2.0 代填", f.UnavailableReason);
        Assert.Equal("", f.Value);
    }

    /// <summary>組一份 GetCapability 回應，capability 欄可自訂（測不符的情境）。</summary>
    private static byte[] CapabilityWith(byte[] capabilityData, uint capability = Tpm2.CapAlgorithms)
    {
        int paramSize = 19 + capabilityData.Length;
        var r = new byte[paramSize];
        r[0] = 0x80; r[1] = 0x01;
        PutBE(r, 2, (uint)paramSize);
        PutBE(r, 6, 0);
        PutBE(r, 11, capability);
        capabilityData.CopyTo(r, 19);
        return r;
    }
}
