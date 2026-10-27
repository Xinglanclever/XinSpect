using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>
/// 建立、保存、驗證及比較硬體時間膠囊。此服務只吃呼叫端組成的 fact collection，
/// 不直接碰 WMI、SMBIOS 或裝置控制碼，因此 UI 可自由整合既有服務，測試也不需要硬體。
/// </summary>
public static partial class HardwareSnapshotService
{
    public const string RedactedValue = "[已遮蔽]";
    public const string RedactedPrefix = "[已遮蔽]:";
    private const int MaxFileBytes = 16 * 1024 * 1024;
    private const int MaxFacts = 100_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>UI 相容入口：使用 fact 中的敏感機器材料產生匿名 ID；沒有時使用內容指紋。</summary>
    public static HardwareSnapshot Create(string appVersion, IEnumerable<HardwareFact> facts, bool includeSensitive)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var source = facts.ToArray();
        var stableIdentity = source.Where(x => x.Key is "system.uuid" or "board.serial")
            .Select(x => x.Value).Where(IsUsableIdentity).Take(1).ToArray();
        var identity = stableIdentity.Length > 0 ? stableIdentity
            : source.Where(x => !x.Sensitive && x.Key is "board.model" or "system.model" or "cpu.name")
                .Select(x => x.Key + "=" + x.Value).ToArray();
        if (identity.Length == 0)
            identity = [string.Join("\0", source.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value))];
        var converted = source.Select(x => new HardwareSnapshotFact
        {
            Key = x.Key,
            Category = x.Category,
            Name = x.Name,
            Value = x.Value,
            // 只有 Present 才帶可計算的數值。Unknown 就算字串裡有數字也不轉——
            // 「未確認」與「可計算」是兩件事，允許它算等於讓未確認的值混進結果。
            NumericValue = x.Availability == FactAvailability.Present ? (x.NumericValue ?? TryNumeric(x.Value)) : null,
            Unit = x.Unit,
            Source = x.Source,
            Trust = x.Trust,
            Sensitive = x.Sensitive,
            MeasuredAtUtc = x.MeasuredAtUtc,
            Availability = x.Availability,
            UnavailableReason = x.UnavailableReason,
        });
        return Create(converted, identity, DateTimeOffset.UtcNow, appVersion,
            includeSensitive ? SensitiveValuePolicy.Preserve : SensitiveValuePolicy.Redact);
    }

    public static Task SaveAsync(string path, HardwareSnapshot snapshot, HardwareSnapshotSaveOptions? options = null)
        => Task.Run(() => Save(path, snapshot, options));

    public static Task<HardwareSnapshot> LoadAsync(string path)
        => Task.Run(() => Load(path));

    public static string AnonymousKey(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.Normalize(NormalizationForm.FormKC))))[..24];

    public static string StableKey(string value)
    {
        string normalized = new(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        normalized = Regex.Replace(normalized, "-+", "-").Trim('-');
        if (normalized.Length is > 0 and <= 80) return normalized;
        return "id-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    }

    public static string TrustText(FactTrustLevel trust) => trust switch
    {
        FactTrustLevel.Measured => "直接量測",
        FactTrustLevel.Derived => "衍生計算",
        FactTrustLevel.Reported => "來源回報",
        _ => "未知",
    };

    /// <summary>
    /// 由原始機器材料產生不可逆匿名識別。材料可由 UUID、主機板或磁碟序號組成，
    /// 只保存加上用途前綴後的 SHA-256，不把任何原始材料放進 snapshot。
    /// </summary>
    public static string CreateAnonymousMachineId(IEnumerable<string?> machineIdentityParts)
    {
        ArgumentNullException.ThrowIfNull(machineIdentityParts);
        var parts = machineIdentityParts
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim().Normalize(NormalizationForm.FormKC))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (parts.Length == 0)
            throw new ArgumentException("至少需要一項非空白的機器識別材料。", nameof(machineIdentityParts));

        string payload = "XinSpect hardware snapshot machine id v1\0" + string.Join("\0", parts);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>建立尚未存檔的 snapshot；完整性摘要會依其內容立即計算。</summary>
    public static HardwareSnapshot Create(
        IEnumerable<HardwareSnapshotFact> facts,
        IEnumerable<string?> machineIdentityParts,
        DateTimeOffset? capturedAtUtc = null,
        string? appVersion = null,
        SensitiveValuePolicy sensitiveValues = SensitiveValuePolicy.Redact)
    {
        string machineId = CreateAnonymousMachineId(machineIdentityParts);
        return CreateWithAnonymousMachineId(facts, machineId, capturedAtUtc, appVersion, sensitiveValues);
    }

    /// <summary>已由安全元件取得匿名 ID 時使用；格式必須是本服務的 SHA-256 ID。</summary>
    public static HardwareSnapshot CreateWithAnonymousMachineId(
        IEnumerable<HardwareSnapshotFact> facts,
        string anonymousMachineId,
        DateTimeOffset? capturedAtUtc = null,
        string? appVersion = null,
        SensitiveValuePolicy sensitiveValues = SensitiveValuePolicy.Redact)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ValidateAnonymousMachineId(anonymousMachineId);
        ValidatePolicy(sensitiveValues);

        var normalized = NormalizeFacts(facts, sensitiveValues);
        var snapshot = new HardwareSnapshot
        {
            SchemaVersion = HardwareSnapshotSchema.CurrentVersion,
            AppVersion = RequireText(appVersion ?? AppInfo.Version, nameof(appVersion), 128),
            AnonymousMachineId = anonymousMachineId,
            CapturedAtUtc = NormalizeUtc(capturedAtUtc ?? DateTimeOffset.UtcNow),
            SensitiveValuesPreserved = sensitiveValues == SensitiveValuePolicy.Preserve,
            Facts = normalized,
            Integrity = new HardwareSnapshotIntegrity
            {
                Algorithm = HardwareSnapshotIntegrity.Sha256Algorithm,
                Hash = new string('0', 64),
            },
        };
        return WithIntegrity(snapshot);
    }

    /// <summary>保存為確定性 UTF-8 JSON；相同 snapshot 會產生逐位元組相同的內容。</summary>
    public static void Save(
        string path,
        HardwareSnapshot snapshot,
        HardwareSnapshotSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new HardwareSnapshotSaveOptions();
        ValidatePolicy(options.SensitiveValues);
        string fullPath = ValidatePath(path, forWrite: true);

        var safe = PrepareForSave(snapshot, options.SensitiveValues);
        byte[] json = Serialize(safe);
        if (json.Length > MaxFileBytes)
            throw new InvalidDataException($"時間膠囊超過 {MaxFileBytes / 1024 / 1024} MiB 上限。");

        string tempPath = fullPath + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            if (!options.Overwrite && File.Exists(fullPath))
                throw new IOException("目標時間膠囊已存在，且未允許覆寫。");
            File.Move(tempPath, fullPath, options.Overwrite);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>載入並驗證 schema、資料限制與 SHA-256 完整性；不接受尾隨或未知 JSON 欄位。</summary>
    public static HardwareSnapshot Load(string path)
    {
        string fullPath = ValidatePath(path, forWrite: false);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("找不到硬體時間膠囊。", fullPath);
        if (info.Length <= 0 || info.Length > MaxFileBytes)
            throw new InvalidDataException("硬體時間膠囊大小無效或超過安全上限。");

        byte[] json = File.ReadAllBytes(fullPath);
        HardwareSnapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<HardwareSnapshot>(json, JsonOptions)
                ?? throw new InvalidDataException("硬體時間膠囊內容是 null。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("硬體時間膠囊不是可接受的 JSON 格式。", ex);
        }

        ValidateSnapshot(snapshot);
        string expected = ComputeIntegrityHash(snapshot);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(snapshot.Integrity.Hash)))
            throw new InvalidDataException("硬體時間膠囊的 SHA-256 完整性驗證失敗；檔案可能已損壞或遭修改。這不是簽章驗證。");
        return Canonicalize(snapshot);
    }

    /// <summary>以穩定 key 比較兩份 snapshot，產出新增、移除、變更與不變。</summary>
    public static HardwareSnapshotDiff Diff(HardwareSnapshot before, HardwareSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ValidateSnapshot(before);
        ValidateSnapshot(after);

        var left = before.Facts.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var right = after.Facts.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var keys = left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
        var entries = new List<HardwareFactChange>();

        foreach (string key in keys)
        {
            left.TryGetValue(key, out var oldFact);
            right.TryGetValue(key, out var newFact);
            var kind = oldFact is null ? SnapshotChangeKind.Added
                : newFact is null ? SnapshotChangeKind.Removed
                : Equivalent(oldFact, newFact) ? SnapshotChangeKind.Unchanged
                : SnapshotChangeKind.Changed;
            entries.Add(new HardwareFactChange
            {
                Key = key,
                Kind = kind,
                Previous = oldFact is null ? null : ToPublicFact(oldFact),
                Current = newFact is null ? null : ToPublicFact(newFact),
                NumericDelta = NumericDelta(oldFact, newFact),
            });
        }

        return new HardwareSnapshotDiff
        {
            BeforeMachineId = before.AnonymousMachineId,
            AfterMachineId = after.AnonymousMachineId,
            BeforeCapturedAtUtc = before.CapturedAtUtc,
            AfterCapturedAtUtc = after.CapturedAtUtc,
            Changes = entries,
        };
    }

    private static HardwareSnapshot PrepareForSave(HardwareSnapshot snapshot, SensitiveValuePolicy policy)
    {
        ValidateSnapshot(snapshot);
        var normalized = NormalizeFacts(snapshot.Facts, policy);
        return WithIntegrity(snapshot with
        {
            CapturedAtUtc = NormalizeUtc(snapshot.CapturedAtUtc),
            SensitiveValuesPreserved = policy == SensitiveValuePolicy.Preserve,
            Facts = normalized,
        });
    }

    private static HardwareSnapshot WithIntegrity(HardwareSnapshot snapshot)
    {
        string hash = ComputeIntegrityHash(snapshot);
        return snapshot with
        {
            Integrity = new HardwareSnapshotIntegrity
            {
                Algorithm = HardwareSnapshotIntegrity.Sha256Algorithm,
                Hash = hash,
            },
        };
    }

    private static string ComputeIntegrityHash(HardwareSnapshot snapshot)
    {
        var unsigned = Canonicalize(snapshot with
        {
            Integrity = new HardwareSnapshotIntegrity
            {
                Algorithm = HardwareSnapshotIntegrity.Sha256Algorithm,
                Hash = "",
            },
        });
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(unsigned, JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(payload));
    }

    private static byte[] Serialize(HardwareSnapshot snapshot)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(Canonicalize(snapshot), JsonOptions);
        var result = new byte[body.Length + 1];
        Buffer.BlockCopy(body, 0, result, 0, body.Length);
        result[^1] = (byte)'\n';
        return result;
    }

    private static HardwareSnapshot Canonicalize(HardwareSnapshot snapshot) => snapshot with
    {
        Facts = snapshot.Facts.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray(),
    };

    private static IReadOnlyList<HardwareSnapshotFact> NormalizeFacts(
        IEnumerable<HardwareSnapshotFact> facts,
        SensitiveValuePolicy policy)
    {
        var list = new List<HardwareSnapshotFact>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            if (fact is null) throw new ArgumentException("事實集合不可包含 null。", nameof(facts));
            var safe = ClampText(fact);
            ValidateFact(safe);
            if (!keys.Add(safe.Key)) throw new ArgumentException($"事實 key 重複：{safe.Key}", nameof(facts));
            list.Add(safe with
            {
                Value = safe.Sensitive && policy == SensitiveValuePolicy.Redact
                    ? IsRedacted(safe.Value) ? safe.Value : RedactedToken(safe.Value)
                    : safe.Value,
                NumericValue = safe.Sensitive && policy == SensitiveValuePolicy.Redact ? null : safe.NumericValue,
                MeasuredAtUtc = NormalizeUtc(safe.MeasuredAtUtc),
            });
            if (list.Count > MaxFacts) throw new ArgumentException($"事實筆數不可超過 {MaxFacts:N0}。", nameof(facts));
        }
        return list.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// 把超過 schema 上限的文字截短，並在末尾留下「原文 N 字」的痕跡。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>為什麼不讓它失敗：</b>一個生產端寫出長句子，不該讓整份時間膠囊存不起來。
    /// 2026-10-26 使用者實際回報的畫面是「儲存失敗／UnavailableReason 超過 512 字元」——
    /// 他做的事是「建立時間膠囊」，而錯誤訊息在講一個他從沒聽過的欄位。
    /// 那條路徑（逐顆碟的 SMART 失敗清單）已經在生產端加上限，這裡是最後一道：
    /// 日後任何新的生產端都不會再用同一種方式讓存檔失敗。
    /// </para>
    /// <para>
    /// <b>但截斷必須看得見。</b>安靜截短會讓「這段話到此為止」被讀成「原因就這麼多」——
    /// 所以保留下原長，而且鍵本身、狀態、數值一律不動（那是內容與判斷，不是敘述）。
    /// </para>
    /// </remarks>
    private static HardwareSnapshotFact ClampText(HardwareSnapshotFact fact) => fact with    {
        Category = Clamp(fact.Category, 128),
        Name = Clamp(fact.Name, 256),
        Source = Clamp(fact.Source, 512),
        // null! 不是為了消警告：.NET 的 required string 允許反序列化出 null，而「值為 null」
        // 本身就是 ValidateFactCore 要如實拒絕的狀態——這裡不替它補一個空字串。
        Value = fact.Value is null ? null! : Clamp(fact.Value, 16_384),
        UnavailableReason = fact.UnavailableReason is null
            ? null
            : string.IsNullOrWhiteSpace(fact.UnavailableReason)
                ? fact.UnavailableReason
                : Clamp(fact.UnavailableReason, 512),
    };

    /// <summary>截到 <paramref name="max"/> 字元以內，末尾註明原文長度。</summary>
    private static string Clamp(string value, int max)
    {
        if (value.Length <= max) return value;
        string marker = TruncationMarker + $"{value.Length:N0} 字，已截斷）";
        int keep = Math.Max(0, max - marker.Length);
        return value[..keep] + marker;
    }

    /// <summary>
    /// 截短痕跡的開頭。<b>只有這一份字面</b>：產生它的地方（<see cref="Clamp"/>）與
    /// 數它的地方（<see cref="CountTruncatedTexts"/>）共用同一個常數，
    /// 否則「回報截斷」會隨著措辭改動變成永遠 0。
    /// </summary>
    public const string TruncationMarker = "……（原文 ";

    /// <summary>
    /// 這份快照裡有幾筆事實的文字被截短過（值或讀不到的原因）。
    /// </summary>
    /// <remarks>
    /// <b>為什麼要數：</b>截斷是「檔案裡看得到、但存檔的人不一定會去翻」的那種資訊。
    /// 建立膠囊之後摘要要說出「有 N 筆說明過長已截斷」，否則使用者會以為自己存的是一份完整紀錄。
    /// 它<b>只回報、不改變</b>——不回填文字，也不把它算成遺失。
    /// </remarks>
    public static int CountTruncatedTexts(HardwareSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Facts.Count(f =>
            (f.UnavailableReason?.Contains(TruncationMarker, StringComparison.Ordinal) ?? false)
            || (f.Value?.Contains(TruncationMarker, StringComparison.Ordinal) ?? false));
    }

    private static void ValidateSnapshot(HardwareSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != HardwareSnapshotSchema.CurrentVersion)
            throw new NotSupportedException($"不支援 schemaVersion {snapshot.SchemaVersion}；目前只支援 {HardwareSnapshotSchema.CurrentVersion}。");
        RequireText(snapshot.AppVersion, nameof(snapshot.AppVersion), 128);
        ValidateAnonymousMachineId(snapshot.AnonymousMachineId);
        if (snapshot.CapturedAtUtc == default) throw new InvalidDataException("capturedAtUtc 不可為預設值。");
        if (snapshot.Facts is null || snapshot.Facts.Count > MaxFacts) throw new InvalidDataException("facts 缺少或超過安全上限。");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in snapshot.Facts)
        {
            if (fact is null) throw new InvalidDataException("facts 不可包含 null。");
            ValidateFact(fact);
            if (!keys.Add(fact.Key)) throw new InvalidDataException($"事實 key 重複：{fact.Key}");
        }
        if (snapshot.Integrity is null
            || !string.Equals(snapshot.Integrity.Algorithm, HardwareSnapshotIntegrity.Sha256Algorithm, StringComparison.Ordinal)
            || !Sha256HexRegex().IsMatch(snapshot.Integrity.Hash ?? ""))
            throw new InvalidDataException("integrity 必須是 64 位小寫十六進位 SHA-256；它不是簽章。");
    }

    /// <summary>
    /// 驗證單筆事實。<b>錯誤訊息一定要指名是哪一把鍵</b>——否則畫面只會說
    /// 「UnavailableReason 超過 512 字元」，而使用者存的是整份時間膠囊，
    /// 既不知道那是什麼欄位，也不知道是哪一筆（2026-10-26 的實際回報）。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不再帶 <c>paramName</c></b>：.NET 會把它接成「(Parameter 'UnavailableReason')」，
    /// 那串字對使用者沒有意義，而且會被原樣貼到畫面上。開發者要的資訊（哪一把鍵、哪個欄位）
    /// 已經在句子裡了。
    /// </remarks>
    private static void ValidateFact(HardwareSnapshotFact fact)
    {
        try { ValidateFactCore(fact); }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"事實 {fact.Key ?? "（無鍵）"}：{ex.Message}");
        }
    }

    private static void ValidateFactCore(HardwareSnapshotFact fact)
    {
        if (!FactKeyRegex().IsMatch(fact.Key ?? ""))
            throw new ArgumentException("鍵不是合法的路徑格式（小寫字母、數字、點、底線、連字號或方括號，最長 160 字元）。");
        RequireText(fact.Category, "分類", 128);
        RequireText(fact.Name, "名稱", 256);
        RequireText(fact.Source, "來源", 512);
        if (!Enum.IsDefined(fact.Availability)) throw new ArgumentException("狀態不是已知的六種之一。");
        if (fact.Availability == FactAvailability.Present)
        {
            // 讀到且確認：值必填，且不得帶「讀不到的原因」。
            RequireText(fact.Value, "值", 16_384);
            if (!string.IsNullOrWhiteSpace(fact.UnavailableReason))
                throw new ArgumentException("這一筆已確認讀到，就不該再帶「讀不到的原因」——兩者互相矛盾。");
        }
        else if (fact.Availability == FactAvailability.Unknown)
        {
            // 有值但未確認：值必填（它的重點就是「拿到了東西」），必須說明為何不敢背書，
            // 且**不得帶 numericValue**——numericValue 是可計算欄位，允許它等於邀請呼叫端
            // 拿未確認的值去算。字串值留給人看，數值要等到確認後才有。
            RequireText(fact.Value, "值", 16_384);
            RequireText(fact.UnavailableReason, "讀不到的原因", 512);
            if (fact.NumericValue is not null)
                throw new ArgumentException("這一筆還沒確認，就不該帶數值欄位——要計算請先確認來源。");
        }
        else
        {
            // 讀不到：值可為空，但必須說明原因，且不得帶數值——誠實原則，不以 0 填補。
            if (fact.Value is null || fact.Value.Length > 16_384) throw new ArgumentException("值過長或沒有值。");
            RequireText(fact.UnavailableReason, "讀不到的原因", 512);
            if (fact.NumericValue is not null) throw new ArgumentException("讀不到的事實不得帶數值。");
        }
        if (fact.Unit is { } unit && unit.Length > 64) throw new ArgumentException("單位過長。");
        if (fact.NumericValue is { } n && (double.IsNaN(n) || double.IsInfinity(n)))
            throw new ArgumentException("數值必須是有限值（NaN 與無限大寫不進 JSON）。");
        if (!Enum.IsDefined(fact.Trust)) throw new ArgumentException("可信度不是已知的值。");
        if (fact.MeasuredAtUtc == default) throw new ArgumentException("沒有一個量測時間——那會讓「這一輪讀的」與「很久以前讀的」長得一樣。");
    }

    private static void ValidateAnonymousMachineId(string value)
    {
        if (!AnonymousIdRegex().IsMatch(value ?? ""))
            throw new ArgumentException("anonymousMachineId 必須是 sha256: 加 64 位小寫十六進位，不可放原始序號。", nameof(value));
    }

    private static string ValidatePath(string path, bool forWrite)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路徑不可空白。", nameof(path));
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.IndexOf('\0') >= 0)
            throw new ArgumentException("路徑包含無效字元。", nameof(path));
        string fullPath = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(fullPath)) throw new ArgumentException("必須使用完整路徑。", nameof(path));
        if (forWrite)
        {
            string? parent = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                throw new DirectoryNotFoundException("目標資料夾不存在；服務不會隱式建立路徑。");
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("基於路徑安全，拒絕直接寫入重新解析點資料夾。");
        }
        return fullPath;
    }

    private static HardwareFact ToPublicFact(HardwareSnapshotFact fact) => new(
        fact.Key, fact.Category, fact.Name, fact.Value, fact.Unit ?? "", fact.Source,
        fact.Trust, fact.Sensitive, fact.MeasuredAtUtc, fact.NumericValue,
        fact.Availability, fact.UnavailableReason);

    private static double? TryNumeric(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
           && double.IsFinite(parsed) ? parsed : null;

    private static double? NumericDelta(HardwareSnapshotFact? before, HardwareSnapshotFact? after)
    {
        if (before?.NumericValue is not { } oldValue || after?.NumericValue is not { } newValue) return null;
        if (!string.Equals(before.Unit ?? "", after.Unit ?? "", StringComparison.Ordinal)) return null;
        double delta = newValue - oldValue;
        return double.IsFinite(delta) ? delta : null;
    }

    private static bool Equivalent(HardwareSnapshotFact a, HardwareSnapshotFact b)
        => string.Equals(a.Category, b.Category, StringComparison.Ordinal)
        && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        && string.Equals(a.Value, b.Value, StringComparison.Ordinal)
        && Nullable.Equals(a.NumericValue, b.NumericValue)
        && string.Equals(a.Unit ?? "", b.Unit ?? "", StringComparison.Ordinal)
        && string.Equals(a.Source, b.Source, StringComparison.Ordinal)
        && a.Trust == b.Trust
        && a.Sensitive == b.Sensitive
        && a.Availability == b.Availability
        && string.Equals(a.UnavailableReason ?? "", b.UnavailableReason ?? "", StringComparison.Ordinal);

    public static bool IsRedacted(string value)
        => value == RedactedValue || value.StartsWith(RedactedPrefix, StringComparison.Ordinal);

    private static string RedactedToken(string value)
        => RedactedPrefix + AnonymousKey("XinSpect sensitive fact v1\0" + value);

    private static bool IsUsableIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string normalized = value.Trim().Replace("-", "", StringComparison.Ordinal).Replace("0", "", StringComparison.Ordinal);
        if (normalized.Length == 0) return false;
        return value.Trim() is not ("—" or "Default string" or "To Be Filled By O.E.M." or "To Be Filled By OEM" or "System Serial Number");
    }

    /// <summary>
    /// 必填且有長度上限的文字。<paramref name="name"/> 是<b>給人看的欄位名</b>（中文），
    /// 不是 C# 屬性名，也刻意不當 <c>paramName</c> 傳——那會讓訊息長出一段
    /// 「(Parameter 'x')」的尾巴，而它對讀訊息的人沒有意義。
    /// </summary>
    private static string RequireText(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name}不可空白。");
        if (value.Length > maxLength) throw new ArgumentException($"{name}超過 {maxLength} 字元。");
        return value;
    }

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value)
        => value.ToUniversalTime();

    private static void ValidatePolicy(SensitiveValuePolicy policy)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
    }

    private static readonly Regex FactKeyPattern = new(
        @"^[a-z0-9](?:[a-z0-9._\-\[\]]{0,159})$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AnonymousIdPattern = new(
        @"^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Sha256HexPattern = new(
        @"^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static Regex FactKeyRegex() => FactKeyPattern;
    private static Regex AnonymousIdRegex() => AnonymousIdPattern;
    private static Regex Sha256HexRegex() => Sha256HexPattern;
}
