namespace XinSpect;

/// <summary>
/// TPM 2.0 的純解碼器（V7 WP14／A31 第一層）：PCR Read 命令建構、回應解析、TCG 事件 log 走訪。
/// 實際通路是 Windows TBS（Tbsip_Submit_Command）——作業系統仲介 TPM 存取，本層只負責位元組，
/// 不碰裝置。防環、防越界、log 損毀時走訪到哪算哪（如實標 truncated），不臆測。
/// </summary>
public static class Tpm2
{
    public const ushort TagNoSessions = 0x8001;
    public const uint CcPcrRead = 0x0000017E;
    public const ushort AlgSha1 = 0x0004, AlgSha256 = 0x000B, AlgSha384 = 0x000C, AlgSha512 = 0x000D, AlgSm3 = 0x0012;

    /// <summary>雜湊演算法 → 摘要長度。未收錄回 0（呼叫方不得猜）。</summary>
    [SpecRef("TPM 2.0 Part 1/2：TPM_ALG_ID 與各演算法摘要長度")]
    public static int DigestSize(ushort alg) => alg switch
    {
        AlgSha1 => 20,
        AlgSha256 => 32,
        AlgSha384 => 48,
        AlgSha512 => 64,
        AlgSm3 => 32,
        _ => 0,
    };

    /// <summary>
    /// 建 TPM2_PCRRead 命令（TPM_ST_NO_SESSIONS）。pcrSelect 以 3 位元組遮罩表達（PCR 0–23）。
    /// </summary>
    [SpecRef("TPM 2.0 Part 3 Commands：TPM_CC_PCRRead＝0x0000017E；命令標頭 tag/paramSize/commandCode；TPMS_PCR_SELECTION { hashAlg u16, sizeofSelect u8, pcrSelect[3] }")]
    public static byte[] BuildPcrReadCommand(uint pcrIndex, ushort hashAlg = AlgSha256)
    {
        if (pcrIndex > 23) throw new ArgumentOutOfRangeException(nameof(pcrIndex), pcrIndex, "PCR 索引只有 0–23（pcrSelect 3 位元組遮罩）。");
        var cmd = new byte[20];
        PutU16(cmd, 0, TagNoSessions);
        PutU32(cmd, 2, 20);
        PutU32(cmd, 6, CcPcrRead);
        PutU32(cmd, 10, pcrIndex);
        PutU16(cmd, 14, hashAlg);
        cmd[16] = 3;
        cmd[17 + (int)(pcrIndex / 8)] = (byte)(1 << (int)(pcrIndex % 8));
        return cmd;
    }

    /// <summary>
    /// 解 TPM2_PCRRead 回應：tag、paramSize、rc、TPM2B_DIGEST。rc≠0 回 null（呼叫方三態帶 rc）。
    /// </summary>
    [SpecRef("TPM 2.0 Part 3：回應標頭（tag u16、paramSize u32、TPM_RC u32）＋TPM2B_DIGEST（u16 size＋位元組）")]
    public static byte[]? DecodePcrResponse(byte[] response, out uint returnCode)
    {
        returnCode = 0;
        if (response.Length < 12) return null;
        uint rc = GetU32(response, 6); // 標頭：tag(0-2)＋paramSize(2-6)＋rc(6-10)
        returnCode = rc;
        if (rc != 0) return null;
        ushort size = GetU16(response, 10);
        if (size == 0 || 12 + size > response.Length) return null;
        return response[12..(12 + size)];
    }

    /// <summary>單一 TCG 事件：PCR 索引、事件型別、摘要筆數（演算法→hex 摘要）。</summary>
    public sealed record TcgEvent(uint PcrIndex, uint EventType, IReadOnlyList<(ushort Alg, byte[] Digest)> Digests);

    /// <summary>走訪結果：事件清單與 truncated 旗標（緩衝提前結束＝log 損毀或截斷，如實標）。</summary>
    public sealed record TcgLogWalk(IReadOnlyList<TcgEvent> Events, bool Truncated);

    /// <summary>
    /// 走訪 TPM2 格式的 TCG 事件 log（TCG_PCR_EVENT2 序列）。任何欄位越界即停（truncated=true），
    /// 走到哪算到哪——損毀的 log 不猜補。
    /// </summary>
    [SpecRef("TCG EFI Protocol Specification（TPM2 事件 log）：TCG_PCR_EVENT2 { PCRIndex u32, EventType u32, TPML_DIGEST_VALUES { count u32, [TPMT_HA { alg u16, digest } ] }, EventSize u32, Event }")]
    public static TcgLogWalk WalkTcgLog(byte[] log)
    {
        var events = new List<TcgEvent>();
        int off = 0;
        while (off + 12 <= log.Length)
        {
            uint pcr = GetU32(log, off);
            uint type = GetU32(log, off + 4);
            uint count = GetU32(log, off + 8);
            if (count > 16) return new TcgLogWalk(events, true); // 合理上限外＝損毀
            off += 12;
            var digests = new List<(ushort, byte[])>((int)count);
            bool overrun = false;
            for (int i = 0; i < count; i++)
            {
                if (off + 2 > log.Length) { overrun = true; break; }
                ushort alg = GetU16(log, off);
                int size = DigestSize(alg);
                if (size == 0 || off + 2 + size > log.Length) { overrun = true; break; }
                digests.Add((alg, log[(off + 2)..(off + 2 + size)]));
                off += 2 + size;
            }
            if (overrun || off + 4 > log.Length) return new TcgLogWalk(events, true);
            uint eventSize = GetU32(log, off);
            off += 4;
            if (off + eventSize > log.Length) return new TcgLogWalk(events, true);
            off += (int)eventSize;
            events.Add(new TcgEvent(pcr, type, digests));
        }
        return new TcgLogWalk(events, off != log.Length); // 恰好用完＝完整
    }

    // TPM2 所有線上格式多欄位都是<b>大端序</b>（TPM 2.0 Part 1 §5）——與 x86 的 BitConverter 相反。
    private static void PutU16(byte[] b, int off, ushort v) { b[off] = (byte)(v >> 8); b[off + 1] = (byte)v; }

    // ── GetCapability：演算法銀行與版本（P2-063／P2-068）────────────────────

    public const uint CcGetCapability = 0x0000017A;
    public const uint CapAlgorithms = 0x00000000;
    public const uint CapTpmProperties = 0x00000006;
    public const uint PtFamilyIndicator = 0x00000100;
    public const uint PtRevision = 0x00000101;
    public const uint PtVendor1 = 0x00000104, PtVendor2 = 0x00000105,
                      PtVendor3 = 0x00000106, PtVendor4 = 0x00000107;

    /// <summary>
    /// 建 TPM2_GetCapability 命令（TPM_ST_NO_SESSIONS）：查演算法或屬性。
    /// </summary>
    [SpecRef("TPM 2.0 Part 3 Commands：TPM_CC_GetCapability＝0x0000017A；命令格式 { capability u32, property u32, propertyCount u32 }")]
    public static byte[] BuildGetCapabilityCommand(uint capability, uint property, uint propertyCount)
    {
        var cmd = new byte[22];
        PutU16(cmd, 0, TagNoSessions);
        PutU32(cmd, 2, 22);
        PutU32(cmd, 6, CcGetCapability);
        PutU32(cmd, 10, capability);
        PutU32(cmd, 14, property);
        PutU32(cmd, 18, propertyCount);
        return cmd;
    }

    /// <summary>capabilityData 的原始位元組（未解讀）。回應格式對不上就回 null，呼叫方如實三態。</summary>
    [SpecRef("TPM 2.0 Part 3：GetCapability 回應標頭（tag／paramSize／rc）＋moreData u8＋capability u32＋"
           + "TPML_ALG_PROPERTY 或 TPML_TAGGED_TPM_PROPERTY（count u32＋條目）")]
    public static byte[]? GetCapabilityData(byte[] response, out uint returnCode, out uint capability, out uint count)
    {
        returnCode = 0; capability = 0; count = 0;
        if (response.Length < 17) return null;
        uint rc = GetU32(response, 6);
        returnCode = rc;
        if (rc != 0) return null;
        uint declared = GetU32(response, 2); // paramSize
        if (declared < 17 || declared > response.Length) return null;
        capability = GetU32(response, 11); // moreData u8@10、capability u32@11
        count = GetU32(response, 15);
        if (count > 4096) return null; // 合理上限外＝格式不符，不硬讀
        return response[19..(int)declared];
    }

    /// <summary>一個演算法屬性條目（TPMT_ALG_PROPERTY）：演算法 ID 與其旗標。</summary>
    public readonly record struct AlgProperty(ushort Alg, uint Flags);

    /// <summary>
    /// 解 TPML_ALG_PROPERTY：count u32@0，其後每條目 6 位元組（alg u16、algProperties u32）。
    /// 越界即停並回已解出的部分——格式不符時寧可少也不猜（呼叫方據此標示）。
    /// </summary>
    [SpecRef("TPM 2.0 Part 2 Structures：TPMT_ALG_PROPERTY { TPMI_ALG_HASH alg; TPMA_ALGORITHM algProperties }；"
           + "TPMA_ALGORITHM bit0 asymmetric、bit1 symmetric、bit2 hash、bit3 object、bit8 signing、bit9 encrypting、bit10 method")]
    public static IReadOnlyList<AlgProperty> DecodeAlgProperties(byte[] capabilityData)
    {
        var list = new List<AlgProperty>();
        if (capabilityData.Length < 4) return list;
        uint count = GetU32(capabilityData, 0);
        if (count > 4096) return list;
        int off = 4;
        for (uint i = 0; i < count; i++)
        {
            if (off + 6 > capabilityData.Length) break;
            list.Add(new AlgProperty(GetU16(capabilityData, off), GetU32(capabilityData, off + 2)));
            off += 6;
        }
        return list;
    }

    /// <summary>TPMA_ALGORITHM 的旗標判讀（未設位元一律為否，不推測）。</summary>
    public static bool IsHash(uint flags) => (flags & 0x04) != 0;
    public static bool IsAsymmetric(uint flags) => (flags & 0x01) != 0;
    public static bool IsSymmetric(uint flags) => (flags & 0x02) != 0;

    /// <summary>演算法 ID → 顯示名。未收錄回 null（呼叫方顯示原始 ID，不猜名）。</summary>
    [SpecRef("TPM 2.0 Part 2：TPM_ALG_ID 值表（本表只收錄常用者，未收錄一律顯示原始 ID）")]
    public static string? AlgName(ushort alg) => alg switch
    {
        AlgSha1 => "SHA-1",
        AlgSha256 => "SHA-256",
        AlgSha384 => "SHA-384",
        AlgSha512 => "SHA-512",
        AlgSm3 => "SM3-256",
        0x0006 => "SHA256-192",
        0x0023 => "SHA3-256",
        0x0024 => "SHA3-384",
        0x0025 => "SHA3-512",
        0x0001 => "RSA",
        _ => null,
    };

    /// <summary>一個 TPM 屬性條目（TPMT_TAGGED_TPM_PROPERTY）：屬性 ID 與其值。</summary>
    public readonly record struct TpmProperty(uint Property, uint Value);

    /// <summary>解 TPML_TAGGED_TPM_PROPERTY：count u32@0，其後每條目 8 位元組（property u32、value u32）。越界即停。</summary>
    [SpecRef("TPM 2.0 Part 2 Structures：TPMT_TAGGED_TPM_PROPERTY { TPM_PT property; UINT32 value }；"
           + "TPM_PT_FAMILY_INDICATOR＝0x100、TPM_PT_REVISION＝0x101、TPM_PT_VENDOR_STRING_1..4＝0x104..0x107")]
    public static IReadOnlyList<TpmProperty> DecodeTpmProperties(byte[] capabilityData)
    {
        var list = new List<TpmProperty>();
        if (capabilityData.Length < 4) return list;
        uint count = GetU32(capabilityData, 0);
        if (count > 4096) return list;
        int off = 4;
        for (uint i = 0; i < count; i++)
        {
            if (off + 8 > capabilityData.Length) break;
            list.Add(new TpmProperty(GetU32(capabilityData, off), GetU32(capabilityData, off + 4)));
            off += 8;
        }
        return list;
    }

    /// <summary>
    /// TPM_PT_FAMILY_INDICATOR 的四位元組（'2.0\0' 的 ASCII 打包）。
    /// 值為 0 或全空白時回 null——「沒回報」不是「版本是 0」。
    /// </summary>
    [SpecRef("TPM 2.0 Part 2：TPM_PT_FAMILY_INDICATOR 為 4 位元組 ASCII（'2.0' 加終止字元），非數值")]
    public static string? FamilyIndicator(uint raw)
    {
        if (raw == 0) return null;
        var bytes = new[] { (byte)(raw >> 24), (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw };
        var text = System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// TPM_PT_REVISION：主版號在 bits 31:24、次版號在 bits 15:8（bits 23:16 為保留），
    /// 例如 1.0 → 0x01000000、1.16 → 0x01001000、1.59 → 0x01003B00。
    /// 值為 0 時回 null——「沒回報」不是「版本是 0」。
    /// <b>這是本專案的解讀慣例</b>；TPM 2.0 Part 2 在 2023 年的修訂把次版號定義成 bits 23:16，
    /// 兩者對「次版號」的取法不同——本方法的引用只主張自己的取法，不宣稱與該修訂一致。
    /// </summary>
    [SpecRef("TPM 2.0 Part 2：TPM_PT_REVISION 為 UINT32；本專案取 bits 31:24 為主版號、bits 15:8 為次版號、中 16 位保留")]
    public static (int Major, int Minor)? Revision(uint raw)
    {
        if (raw == 0) return null;
        return ((int)((raw >> 24) & 0xFF), (int)((raw >> 8) & 0xFF));
    }

    private static void PutU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16); b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
    }
    private static ushort GetU16(byte[] b, int off) => (ushort)((b[off] << 8) | b[off + 1]);
    private static uint GetU32(byte[] b, int off)
        => ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];
}
