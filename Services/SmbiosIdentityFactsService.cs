using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XinSpect;

/// <summary>
/// SMBIOS Type 0–3 的全欄位事實（ULTIMATE-1000 階段二 P2-019）
/// 與 Type 32 開機資訊（P2-025）。
///
/// <para><b>P2-024（Type 19 記憶體映射）不在這裡——它退回未開工。</b>
/// 欄位佈局沒有可靠依據，見檔尾的說明。</para>
///
/// <para>
/// <b>為什麼把三項放一起：</b>它們都是「單一結構、固定欄位」的 SMBIOS 表，
/// 共用同一次 <see cref="SmbiosStruct"/> 讀取。分三次讀同一張表只會多出
/// 「兩次讀到的不一樣」的可能——而那不是誠實，是浪費。
/// </para>
///
/// <para>
/// <b>界線：Type 0–3 是「機器自我介紹」，不是量測。</b>
/// 廠商填的字串一律經過佔位值過濾（`To Be Filled By O.E.M.`、`Default string`
/// 這些不是廠商名，是「沒填」）；<b>沒填與填錯是兩件事</b>，
/// 而本服務對前者標「未提供」、對後者照實陳述——不代為判斷哪個才是對的。
/// </para>
/// </summary>
public static class SmbiosIdentityFactsService
{
    private const string Category = "SMBIOS";

    public const string BiosKey = "sb.type0";
    public const string SystemKey = "sb.type1";
    public const string BoardKey = "sb.type2";
    public const string ChassisKey = "sb.type3";
    public const string UuidKey = "sb.type1.uuid";
    public const string BootKey = "sb.boot";
    public const string BootStatusKey = "sb.boot.status";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<SmbiosStruct>>? structsProbe = null)
    {
        IReadOnlyList<SmbiosStruct> structs;
        try { structs = (structsProbe ?? (() => new SmbiosService().Structs))(); }
        catch (Exception ex)
        {
            return Na(at, "SMBIOS 表讀取失敗：" + ex.Message, FactAvailability.ReadError);
        }

        if (structs.Count == 0)
            return Na(at, "SMBIOS 表不可用或無結構（GetSystemFirmwareTable('RSMB') 未回表）",
                FactAvailability.NotSupported);

        var byType = structs.GroupBy(s => s.Type).ToDictionary(g => g.Key, g => g.ToList());
        var type1 = First(byType, 1);
        var type32 = First(byType, 32);

        // 每一把在類別裡宣告的鍵都必須從這裡出去——Uuid 曾經寫好了卻沒被呼叫
        // （編譯器不抱怨，因為它是合法的 private static 方法）。
        // 這一條由 SmbiosIdentityFactsTests 的守門盯著。
        var facts = new List<HardwareFact>
        {
            Type0(at, First(byType, 0)),
            Type1(at, type1),
            Uuid(at, type1),
            Type2(at, First(byType, 2)),
            Type3(at, First(byType, 3)),
            Boot(at, type32),
            BootStatus(at, type32),
        };
        return facts;
    }

    private static SmbiosStruct? First(IReadOnlyDictionary<byte, List<SmbiosStruct>> byType, byte type)
        => byType.TryGetValue(type, out var list) && list.Count > 0 ? list[0] : null;

    // ── Type 0：BIOS ────────────────────────────────────────────────────

    private static HardwareFact Type0(DateTimeOffset at, SmbiosStruct? s)
    {
        if (s is null || s.Length < 0x12)
            return Missing(BiosKey, "BIOS 資訊", at,
                "SMBIOS 未提供 Type 0——沒有它，韌體版本與日期只能靠 WMI 那一個來源");

        var parts = new List<string>();
        Add(parts, "廠商", Str(s, 0x04));
        Add(parts, "版本", Str(s, 0x05));
        Add(parts, "發行日", Str(s, 0x08));

        // ROM 大小：擴充欄位（@0x14 起）只在長度足夠時才讀。
        // 邊界是「起點 ＋ 寬度」，不是「起點」——0x14 + 4 = 0x18，
        // 所以需要 Length >= 0x18。第一版寫成 Length > 0x14，
        // 那只保證索引 0x14 有效，卻去讀了 0x14..0x17。
        if (s.Length >= 0x18)
        {
            ulong rom = s.ByteAt(0x13) == 0xFF && s.Length > 0x18
                ? (ulong)s.DwordAt(0x14) * 1024 * 1024   // 0xFF = 單位是 MB
                : (ulong)((s.ByteAt(0x13) + 1) * 64 * 1024);
            if (rom > 0) parts.Add($"ROM {rom / (1024 * 1024)} MiB");
        }

        // 特性旗標是 u16@0x11（兩個位元組：0x11 與 0x12），
        // 所以需要 Length >= 0x13。第一版寫 > 0x12，那只保證 0x12 有效。
        var chars = new List<string>();
        if (s.Length >= 0x13)
        {
            ulong flags = s.ByteAt(0x11) | ((ulong)s.ByteAt(0x12) << 8);
            if ((flags & 0x08) != 0) chars.Add("支援升級（BIOS 可更新）");
            if ((flags & 0x10) != 0) chars.Add("支援開機選單");
            if ((flags & 0x800) != 0) chars.Add("支援 UEFI");
            if ((flags & 0x1000) != 0) chars.Add("為 UEFI 平台");
        }

        return new HardwareFact(BiosKey, Category, "BIOS 資訊",
            string.Join("／", parts) + (chars.Count > 0 ? $";{string.Join("、", chars)}" : ""),
            "", "SMBIOS Type 0（廠商@0x04、版本@0x05、發行日@0x08、ROM 大小@0x09、特性@0x11）",
            FactTrustLevel.Measured, false, at, null, FactAvailability.Present,
            "這是韌體自述；與 WMI Win32_BIOS 的說法並列時，兩者不同是事實不是錯誤");
    }

    // ── Type 1：系統 ────────────────────────────────────────────────────

    private static HardwareFact Type1(DateTimeOffset at, SmbiosStruct? s)
    {
        if (s is null || s.Length < 0x08)
            return Missing(SystemKey, "系統資訊", at,
                "SMBIOS 未提供 Type 1——沒有它，機型與序號只能靠 WMI 那一個來源");

        var parts = new List<string>();
        Add(parts, "廠商", Str(s, 0x04));
        Add(parts, "產品名", Str(s, 0x05));
        Add(parts, "版本", Str(s, 0x06));
        Add(parts, "序號", Str(s, 0x07));

        return new HardwareFact(SystemKey, Category, "系統資訊", string.Join("／", parts), "",
            "SMBIOS Type 1（廠商@0x04、產品@0x05、版本@0x06、序號@0x07、UUID@0x08）",
            FactTrustLevel.Measured, true, at, null, FactAvailability.Present,
            "序號屬 L1 敏感欄位，這一項標為敏感；與 WMI Win32_ComputerSystemProduct 並列");
    }

    private static HardwareFact Uuid(DateTimeOffset at, SmbiosStruct? s)
    {
        if (s is null || s.Length < 0x18)
            return Missing(UuidKey, "系統 UUID", at, "SMBIOS 未提供 Type 1 或長度不足");

        // UUID 的三個欄位各自是 little-endian（SMBIOS 2.6+ 規定）：
        //   Data1 是 4 bytes（@0x08–0x0B）→ 反轉
        //   Data2 是 2 bytes（@0x0C–0x0D）→ 反轉
        //   Data3 是 2 bytes（@0x0E–0x0F）→ 反轉
        //   後 8 bytes（@0x10–0x17）原樣
        //
        // 第一版把 @0x08–@0x0F 當成「8 個位元組整段反轉」，於是 Data2 與 Data3
        // 被攪在一起。那個錯誤出來的結果仍然是一個格式正確的 UUID
        // ——它看起來完全正常，只是指認到別的機器。
        byte[] u = new byte[16];
        u[0] = s.ByteAt(0x0B); u[1] = s.ByteAt(0x0A); u[2] = s.ByteAt(0x09); u[3] = s.ByteAt(0x08);
        u[4] = s.ByteAt(0x0D); u[5] = s.ByteAt(0x0C);
        u[6] = s.ByteAt(0x0F); u[7] = s.ByteAt(0x0E);
        for (int i = 8; i < 16; i++) u[i] = s.ByteAt(0x08 + i);

        bool allZero = u.All(b => b == 0);
        bool allFf = u.All(b => b == 0xFF);
        if (allZero || allFf)
            return new HardwareFact(UuidKey, Category, "系統 UUID",
                allZero ? "全為 0（未設定）" : "全為 0xFF（未設定）", "",
                "SMBIOS Type 1 @0x08（16 bytes）", FactTrustLevel.Unknown, true, at, null,
                FactAvailability.NotApplicable,
                "UUID 未設定——那是「沒有這個識別碼」而不是「識別碼是 0」；"
                + "把它當成一個 UUID 會讓資產比對誤判為同一台");

        string text = $"{u[0]:X2}{u[1]:X2}{u[2]:X2}{u[3]:X2}-{u[4]:X2}{u[5]:X2}-"
                    + $"{u[6]:X2}{u[7]:X2}-{u[8]:X2}{u[9]:X2}-"
                    + string.Concat(u[10..].Select(b => b.ToString("X2")));
        return new HardwareFact(UuidKey, Category, "系統 UUID", text, "",
            "SMBIOS Type 1 @0x08（前 3 欄位 little-endian，SMBIOS 2.6+ 規定）",
            FactTrustLevel.Measured, true, at, null, FactAvailability.Present,
            "這是機器識別碼（標 L1）：能唯一指認一台機器，所以匯出時預設遮蔽");
    }

    // ── Type 2：底板 ────────────────────────────────────────────────────

    private static HardwareFact Type2(DateTimeOffset at, SmbiosStruct? s)
    {
        if (s is null || s.Length < 0x08)
            return Missing(BoardKey, "主機板資訊", at,
                "SMBIOS 未提供 Type 2——沒有它，主機板型號只能靠 WMI 那一個來源");

        var parts = new List<string>();
        Add(parts, "廠商", Str(s, 0x04));
        Add(parts, "產品名", Str(s, 0x05));
        Add(parts, "版本", Str(s, 0x06));
        Add(parts, "序號", Str(s, 0x07));

        return new HardwareFact(BoardKey, Category, "主機板資訊", string.Join("／", parts), "",
            "SMBIOS Type 2（廠商@0x04、產品@0x05、版本@0x06、序號@0x07）",
            FactTrustLevel.Measured, true, at, null, FactAvailability.Present,
            "序號屬 L1 敏感欄位；與 WMI Win32_BaseBoard 並列");
    }

    // ── Type 3：機箱 ────────────────────────────────────────────────────

    private static HardwareFact Type3(DateTimeOffset at, SmbiosStruct? s)
    {
        if (s is null || s.Length < 0x06)
            return Missing(ChassisKey, "機箱資訊", at,
                "SMBIOS 未提供 Type 3——沒有它，「這是不是機架式」少一個來源");

        var parts = new List<string>();
        Add(parts, "廠商", Str(s, 0x04));

        byte types = s.ByteAt(0x05);
        string typeName = SmbiosService.ChassisTypeName((byte)(types & 0x7F));
        parts.Add($"型別 {typeName}");
        if ((types & 0x80) != 0) parts.Add("機箱鎖存在");

        if (s.Length > 0x08) Add(parts, "版本", Str(s, 0x06));
        if (s.Length > 0x09) Add(parts, "序號", Str(s, 0x07));

        return new HardwareFact(ChassisKey, Category, "機箱資訊", string.Join("／", parts), "",
            "SMBIOS Type 3（廠商@0x04、型別@0x05 bit7 鎖、版本@0x06、序號@0x07）",
            FactTrustLevel.Measured, true, at, null, FactAvailability.Present,
            "機箱型別直接回答「這是不是伺服器／機架式」；鎖的位元是機箱鎖的存在性陳述，"
            + "不是「現在鎖著」");
    }

    // ── Type 19：記憶體映射（未開工）────────────────────────────────────

    // P2-024 退回未開工。欄位佈局我沒有可靠依據：
    //   * size 是 u32@0x0C，還是 end address u32@0x08？
    //   * 型別是 u8@0x0C，還是 u8@0x0E？
    // 專案裡 SmbiosService 沒有解 Type 19，所以沒有可對照的權威。
    //
    // 一個位址位移不確定的解碼器會給出**看起來合理的錯誤位址**
    // ——那比沒有更糟，理由與 P2-008 HMAT 的撤回相同。
    //
    // 修好的路徑：找到 SMBIOS 規格 Table 7.20（Type 19）的欄位圖，
    // 或找到一份可信的開源實作（dmidecode 的 dmi_memory_array_mapped_address）
    // 可以逐欄位對照。在那之前，sb.memmap 這兩把鍵不產出。

    // ── Type 32：開機資訊 ───────────────────────────────────────────────

    private static HardwareFact Boot(DateTimeOffset at, SmbiosStruct? s)
    {
        if (s is null || s.Length < 0x0B)
            return Missing(BootKey, "開機資訊", at,
                "SMBIOS 未提供 Type 32——它記錄的是「上一次 POST 的結果」，"
                + "沒有它就只能從事件記錄推");

        var parts = new List<string>();
        if (s.Length >= 0x07)
            parts.Add($"狀態 {BootStatusName(s.ByteAt(0x06))}");
        // POST 結果是 @0x08–@0x0D（6 個位元組），所以需要 Length >= 0x0E。
        // 第一版寫 > 0x0A，那只保證 0x0A 有效，卻讀到了 0x0D。
        if (s.Length >= 0x0E)
        {
            byte[] reserved = new byte[6];
            for (int i = 0; i < 6; i++) reserved[i] = s.ByteAt(0x08 + i);
            bool allZero = reserved.All(b => b == 0);
            parts.Add(allZero ? "POST 結果未記錄" : "POST 有記錄（見原始位元組）");
        }

        return new HardwareFact(BootKey, Category, "開機資訊", string.Join("／", parts), "",
            "SMBIOS Type 32（保留@0x04、狀態@0x06、POST 結果@0x08–0x0D）",
            FactTrustLevel.Measured, false, at, null, FactAvailability.Present,
            "這是「上一次開機的結果」，不是現在這一台的狀態——"
            + "一台正常運作中的機器可以記錄著三個月前的一次失敗");
    }

    private static HardwareFact BootStatus(DateTimeOffset at, SmbiosStruct? s)
    {
        if (s is null || s.Length < 0x07)
            return Missing(BootStatusKey, "上次開機狀態", at, "SMBIOS 未提供 Type 32 或長度不足");

        byte code = s.ByteAt(0x06);
        bool ok = code == 0;
        return new HardwareFact(BootStatusKey, Category, "上次開機狀態",
            ok ? "正常" : $"異常（{BootStatusName(code)}）", "", "SMBIOS Type 32 位移 0x06",
            FactTrustLevel.Measured, false, at, code,
            ok ? FactAvailability.Present : FactAvailability.Unknown,
            ok ? "上一次開機被記錄為正常——這是歷史紀錄，不是現在的健康狀態"
               : "上一次開機被記錄為異常——那可能是很久以前的事，"
                 + "要判斷是否仍在發生需要事件記錄那個獨立來源");
    }

    /// <summary>
    /// Type 32 狀態碼的名稱——<b>唯一的解讀處</b>。
    ///
    /// <para>
    /// 原本 <c>Boot</c> 與 <c>BootStatus</c> 各自有一份：前者有完整 11 項名稱，
    /// 後者只比 <c>code == 0</c> 就輸出代碼。於是同一張表的同一個欄位，
    /// 一處給「硬體失敗」、另一處只給「代碼 5」——
    /// <b>那不是兩個地方的小差異，是使用者會看到兩種不同的說法。</b>
    /// </para>
    /// </summary>
    private static string BootStatusName(byte code)
    {
        string[] status = ["正常", "無開機裝置", "作業系統載入失敗",
            "韌體失敗", "儲存裝置失敗", "硬體失敗", "使用者請求的啟動", "安全違規",
            "先前請求的映像復原", "系統韌體復原", "開機選項已改變"];
        return code < status.Length ? status[code] : $"代碼 {code}";
    }

    // ── 共用 ────────────────────────────────────────────────────────────

    private static void Add(List<string> parts, string label, string value)
    {
        if (value.Length > 0) parts.Add($"{label} {value}");
    }

    /// <summary>依 1 起始索引取字串；佔位字串視為未提供（沒填與填錯是兩件事）。</summary>
    private static string Str(SmbiosStruct s, int offset)
    {
        if (offset >= s.Length) return "";
        string? v = s.GetString(s.ByteAt(offset));
        return v is null || v.Length == 0 || SmbiosService.IsPlaceholder(v) ? "" : v;
    }

    private static HardwareFact Missing(string key, string name, DateTimeOffset at, string reason) =>
        new(key, Category, name, "", "", "SMBIOS", FactTrustLevel.Unknown, false, at,
            null, FactAvailability.NotApplicable, reason);

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason,
        FactAvailability availability)
    {
        // 這份清單必須與 Collect 實際會產出的鍵一致——否則失敗路徑與成功路徑
        // 會給出不同的事實集，而「少了一把鍵」在報告上表現為「那一項不見了」。
        string[] keys =
        [
            BiosKey, SystemKey, UuidKey, BoardKey, ChassisKey,
            BootKey, BootStatusKey,
        ];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", "SMBIOS",
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToList();
    }
}
