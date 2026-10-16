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

    // ═══ 本區的常數收錄政策（spec-ref-policy）═══
    //
    // **只寫對準過出處的編號，其餘一律不寫**——憑記憶補上的編號會讓一個不存在的屬性
    // 看起來存在，而那比「未收錄」更糟：**未收錄看得出來，填錯看不出來。**
    //
    // 出處＝TPM 2.0 Part 2 Structures 的 "TPM_PT Constants" 表。該表的結構是算術的：
    //   TPM_PT_GROUP＝0x100（每組的數量）→ TPM_PT_FIXED＝GROUP×1＝0x100、TPM_PT_VAR＝GROUP×2＝0x200，
    //   組內第 n 項＝基數＋n。所以 FIXED 的第 0..6 項依序是
    //   FAMILY_INDICATOR／LEVEL／REVISION／DAY_OF_YEAR／YEAR／MANUFACTURER／VENDOR_STRING_1。
    // 逐項核對來源：tpm2-software/tpm2-tss 的 `tss2_tpm2_types.h`（由規格表產生的標頭；
    // v4.0.1（2022）與現行 master 的註解一致）。
    //
    // **2026-10-17 修過一次真的錯**：先前 `PtRevision` 寫 0x101（那是 LEVEL）、
    // `PtVendor1..4` 寫 0x104..0x107（那是 YEAR／MANUFACTURER／VENDOR_STRING_1..2），
    // 而 `Revision()` 依「主版號在 bits 31:24、次版號在 bits 15:8」拆位——但規格對這一項寫的是
    // **「the specification Revision times 100」**（例：Revision 01.01 → 101）。
    // 本機 `Win32_Tpm` 的 `SpecVersion` 是「2.0, 0, 1.16」：family 2.0、LEVEL 0、revision 1.16
    // ⇒ 真值應是 116，舊寫法會讀成 0.116，而且因為它讀的是 LEVEL（0）而顯示「未回報」，
    // 再把 YEAR 與 MANUFACTURER 當成廠商字串的頭兩段。**那是「讀得到、看起來合理、
    // 但不是它標的那一項」**——與 SRUM／SPI 那兩次同一種病。
    // 修法照老規矩：對準出處、改值、並在測試用 golden 把「哪個編號是哪一項」钉住。
    // **舊版測試的假資料是照著錯的假設造的，所以一路綠——自我一致的假資料證明不了任何事。**
    //
    // 已對準的：FIXED 0x100–0x10C 與 VAR 0x200–0x201。**其餘 TPM_PT_*（HR 計數、NV、
    // lockout 那一族）仍不收錄**，要用時先查表再寫，不要從別的程式碼庫抄——那些庫可能有自己的理由。

    public const uint CcGetCapability = 0x0000017A;
    public const uint CapAlgorithms = 0x00000000;
    public const uint CapTpmProperties = 0x00000006;

    // ── TPM_PT_FIXED（0x100 起）：只隨韌體變 ──────────────────────────────
    public const uint PtFamilyIndicator = 0x00000100;   // FIXED + 0：四位元組 ASCII（'2.0'＋終止字元）
    public const uint PtLevel = 0x00000101;             // FIXED + 1：規格 Level（TPM 2.0 為 0）
    public const uint PtRevision = 0x00000102;          // FIXED + 2：規格版號 ×100（1.16 → 116）
    public const uint PtDayOfYear = 0x00000103;         // FIXED + 3：規格日期（TCG 曆的日序）
    public const uint PtYear = 0x00000104;              // FIXED + 4：規格年份（西元）
    public const uint PtManufacturer = 0x00000105;      // FIXED + 5：廠商 ID（四位元組 ASCII，如 INTC）
    public const uint PtVendor1 = 0x00000106;           // FIXED + 6..9：廠商字串 1..4（合計 16 位元組）
    public const uint PtVendor2 = 0x00000107;
    public const uint PtVendor3 = 0x00000108;
    public const uint PtVendor4 = 0x00000109;
    public const uint PtVendorTpmType = 0x0000010A;     // FIXED + 10：廠商自訂型別碼
    public const uint PtFirmwareVersion1 = 0x0000010B;  // FIXED + 11：韌體版號（高位）
    public const uint PtFirmwareVersion2 = 0x0000010C;  // FIXED + 12：韌體版號（低位）

    // ── TPM_PT_VAR（0x200 起）：隨狀態變 ────────────────────────────────
    public const uint PtPermanent = 0x00000200;         // VAR + 0：TPMA_PERMANENT（擁有權／清除／鎖定）
    public const uint PtStartupClear = 0x00000201;      // VAR + 1：TPMA_STARTUP_CLEAR（三個階層的啟用）

    /// <summary>
    /// 一次讀取的固定屬性項數：0x100 起的十項（family 到 VENDOR_STRING_4）。
    /// 由 <see cref="PtFamilyIndicator"/> 起算十項——少一項就會少讀後面的廠商字串。
    /// </summary>
    public const uint FixedPropertyCount = 10;

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
    [SpecRef("TPM 2.0 Part 2 Structures：TPMT_TAGGED_TPM_PROPERTY { TPM_PT property; UINT32 value }。"
           + "**屬性編號的出處見本檔「常數收錄政策」那一節**：已對準 FIXED 0x100–0x10C 與 VAR 0x200–0x201，"
           + "其餘 TPM_PT_* 未收錄（未收錄看得出來，填錯看不出來）。")]
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
    /// 四位元組字串屬性的 ASCII 打包（family indicator／manufacturer／廠商字串的每一段共用）。
    /// TPM 2.0 線上格式為大端序，所以最左邊的位元組是第一個字元。
    /// 未列印字元（含 0x00 補位）不在此過濾，交由呼叫端 <c>TrimEnd('\0', ' ')</c> 處理。
    /// </summary>
    [SpecRef("TPM 2.0 Part 1 §5：多欄位為大端序；Part 2：TPM_PT 家族的字串屬性為 4 位元組 ASCII 打包")]
    public static string Ascii4(uint raw)
    {
        var bytes = new[] { (byte)(raw >> 24), (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw };
        return System.Text.Encoding.ASCII.GetString(bytes);
    }

    /// <summary>
    /// TPM_PT_FAMILY_INDICATOR 的四位元組（'2.0\0' 的 ASCII 打包）。
    /// 值為 0 或全空白時回 null——「沒回報」不是「版本是 0」。
    /// </summary>
    [SpecRef("TPM 2.0 Part 2：TPM_PT_FAMILY_INDICATOR 為 4 位元組 ASCII（'2.0' 加終止字元），非數值")]
    public static string? FamilyIndicator(uint raw)
    {
        if (raw == 0) return null;
        var text = Ascii4(raw).TrimEnd('\0', ' ');
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// TPM_PT_REVISION：值＝<b>規格版號 ×100</b>（規格原文「the specification Revision times 100.
    /// EXAMPLE Revision 01.01 would have a value of 101」）。TPM 2.0 函式庫規格的版號是 1.xx，
    /// 所以本機回報的 116 讀成 1.16——與 Windows <c>Win32_Tpm.SpecVersion</c> 的第 3 欄一致。
    /// <para>
    /// <b>值的形狀不對時回 null，不硬拆</b>：舊版把它當成「主版號在 bits 31:24、次版號在 bits 15:8」，
    /// 於是 116 會讀成「0.116」、而 0x01000300 那種測試值還會讀成「1.3」（真機不會回那個）。
    /// 形狀檢查＝0 < 值 ≤ 9999（三位以內的主/次版號）；超出代表這不是 ×100 的編碼，如實說沒回報。
    /// </para>
    /// </summary>
    [SpecRef("TPM 2.0 Part 2 Structures：TPM_PT_REVISION＝規格版號 ×100（EXAMPLE：Revision 01.01 → 101）")]
    public static (int Major, int Minor)? Revision(uint raw)
    {
        if (raw == 0 || raw > 9999) return null;
        return ((int)(raw / 100), (int)(raw % 100));
    }

    /// <summary>TPMA_PERMANENT（<see cref="PtPermanent"/> 的值）：本專案用到的位元。</summary>
    public readonly record struct TpmaPermanent(
        bool OwnerAuthSet, bool EndorsementAuthSet, bool LockoutAuthSet,
        bool DisableClear, bool InLockout, bool TpmGeneratedEps);

    /// <summary>
    /// 解 TPMA_PERMANENT。這些位元代表「自上次 TPM2_Clear 以來發生過的事」——
    /// <c>OwnerAuthSet</c> 就是一般說的「已取得擁有權」（storage 階層的授權值被改過）。
    /// 保留位不讀、也不回值。
    /// </summary>
    [SpecRef("TPM 2.0 Part 2 Structures：TPMA_PERMANENT——ownerAuthSet＝bit0、endorsementAuthSet＝bit1、"
           + "lockoutAuthSet＝bit2、disableClear＝bit8、inLockout＝bit9、tpmGeneratedEPS＝bit10（bits 3–7 與 11–31 保留）")]
    public static TpmaPermanent DecodePermanent(uint raw) => new(
        OwnerAuthSet: (raw & 0x00000001u) != 0,
        EndorsementAuthSet: (raw & 0x00000002u) != 0,
        LockoutAuthSet: (raw & 0x00000004u) != 0,
        DisableClear: (raw & 0x00000100u) != 0,
        InLockout: (raw & 0x00000200u) != 0,
        TpmGeneratedEps: (raw & 0x00000400u) != 0);

    /// <summary>TPMA_STARTUP_CLEAR（<see cref="PtStartupClear"/> 的值）：本專案用到的位元。</summary>
    public readonly record struct TpmaStartupClear(
        bool PhEnable, bool ShEnable, bool EhEnable, bool PhEnableNv, bool Orderly);

    /// <summary>
    /// 解 TPMA_STARTUP_CLEAR。三個 Enable 位分別對應平台／儲存／背書階層是否可用
    /// （關掉的那個階層，其物件與 NV 索引都用不到）；<c>Orderly</c> 是「上次有先關機再開機」。
    /// </summary>
    [SpecRef("TPM 2.0 Part 2 Structures：TPMA_STARTUP_CLEAR——phEnable＝bit0、shEnable＝bit1、"
           + "ehEnable＝bit2、phEnableNV＝bit3、orderly＝bit31（bits 4–30 保留）")]
    public static TpmaStartupClear DecodeStartupClear(uint raw) => new(
        PhEnable: (raw & 0x00000001u) != 0,
        ShEnable: (raw & 0x00000002u) != 0,
        EhEnable: (raw & 0x00000004u) != 0,
        PhEnableNv: (raw & 0x00000008u) != 0,
        Orderly: (raw & 0x80000000u) != 0);

    private static void PutU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16); b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
    }
    private static ushort GetU16(byte[] b, int off) => (ushort)((b[off] << 8) | b[off + 1]);
    private static uint GetU32(byte[] b, int off)
        => ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];
}
