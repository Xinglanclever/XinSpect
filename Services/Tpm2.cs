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
    private static void PutU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16); b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
    }
    private static ushort GetU16(byte[] b, int off) => (ushort)((b[off] << 8) | b[off + 1]);
    private static uint GetU32(byte[] b, int off)
        => ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];
}
