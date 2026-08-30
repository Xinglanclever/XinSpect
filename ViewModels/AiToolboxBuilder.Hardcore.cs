using System.Text;

namespace XinSpect;

/// <summary>
/// AI 診斷代理的第三批唯讀工具：硬核單元（Top-down 管線歸因、頻率真相、平台可信度、
/// BIOS 與 ME 微碼、逐核時間歸因、電源政策、記憶體真實面貌、機器檢查與 WHEA、
/// 處理器免疫位元、RDT 快取／頻寬監測）。
///
/// 這批與前兩批有一個關鍵差別：**它們不會在啟動時自己讀值**，多半要使用者開到對應頁面
/// （或按下該頁的量測按鈕）才有資料。因此每個工具都必須先判斷「有沒有量過」，
/// 沒量過就明說「本機尚未量測，請開啟某頁按某鈕」——絕不把 0 或「—」當成量到的結果回給模型，
/// 也絕不由代理自己去發動量測（那會在使用者沒開口時動用 PMU 與 MSR）。
/// </summary>
internal static partial class AiToolboxBuilder
{
    /// <summary>尚未量測時的統一說法：告訴模型「沒有資料」而不是「數值是 0」。</summary>
    private static string NotMeasured(string what, string where)
        => $"（{what}尚未量測。這項不會自動讀取，需要使用者在「{where}」按下量測按鈕；"
         + "在那之前本機沒有這項資料——請如實告知使用者，不要用 0 或推估值代替。）";

    /// <summary>判斷一個顯示字串是不是「還沒有值」的占位符。</summary>
    private static bool Blank(string? s)
        => string.IsNullOrWhiteSpace(s) || s == "—" || s.StartsWith("尚未");

    /// <summary>把逐核／逐列資料截到前 n 列，並註明總共幾列（避免 36 顆邏輯處理器灌滿上下文）。</summary>
    private static void Rows<T>(StringBuilder sb, IReadOnlyList<T> rows, int max, Func<T, string> fmt)
    {
        int take = Math.Min(max, rows.Count);
        for (int i = 0; i < take; i++) sb.AppendLine("  " + fmt(rows[i]));
        if (rows.Count > take) sb.AppendLine($"  （共 {rows.Count} 列，以上為前 {take} 列）");
    }

    public static void AddHardcore(AiToolbox box, MainViewModel vm)
    {
        AddTopDown(box, vm);
        AddFrequencyTruth(box, vm);
        AddPlatformTrust(box, vm);
        AddFirmwareTruth(box, vm);
        AddCoreTime(box, vm);
        AddPowerPolicy(box, vm);
        AddMemoryTruth(box, vm);
        AddMachineCheck(box, vm);
        AddSecurityBits(box, vm);
        AddRdt(box, vm);
    }

    // ── Top-down 管線歸因 ───────────────────────────────────────────────────

    private static void AddTopDown(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_topdown_pipeline",
            "取得處理器管線的 Top-down Level 1 歸因（以 PMU 實測）：Retiring／Bad Speculation／"
            + "Frontend Bound／Backend Bound 四項占比、每週期退休槽數，以及逐核明細。"
            + "要回答「這台機器現在慢在哪個環節」時用這個。此項需使用者先在 CPU 頁按「開始取樣」。",
            _ =>
            {
                var s = vm.TopDown;
                var sb = new StringBuilder();
                Line(sb, "PMU 支援", s.SupportText);
                if (Blank(s.VerdictText) && s.Buckets.Count == 0)
                    return NotMeasured("Top-down 管線歸因", "處理器 › Top-down 管線歸因")
                         + (Blank(s.SupportText) ? "" : "\nPMU 支援：" + s.SupportText);

                Line(sb, "取樣狀態", s.Phase);
                Line(sb, "結論", s.VerdictText);
                Line(sb, "每週期退休槽數", s.SlotsRetiredText);
                if (s.Buckets.Count > 0)
                {
                    sb.AppendLine("四大歸因：");
                    foreach (var b in s.Buckets)
                        sb.AppendLine($"  {b.Name} {b.PercentText}"
                            + (string.IsNullOrWhiteSpace(b.Note) ? "" : $"（{b.Note}）"));
                }
                if (s.Rows.Count > 0)
                {
                    sb.AppendLine("逐核明細（Retiring／BadSpec／Frontend／Backend）：");
                    Rows(sb, s.Rows, 12, r => $"{r.CoreText}：{r.RetiringText}／{r.BadSpecText}"
                                            + $"／{r.FrontendText}／{r.BackendText}");
                }
                sb.AppendLine("量測限制（轉述時必須一併說明）：Bad Speculation 未計入 INT_MISC.RECOVERY_CYCLES 項"
                    + "（一般用途計數器只有 4 個），故 Bad Speculation 略微低估、Backend Bound 略微高估；"
                    + "逐核為循序取樣，不是同一瞬間的快照；本工具不提供 IPC。");
                Line(sb, "說明", s.StatusLine);
                return Done(sb, NotMeasured("Top-down 管線歸因", "處理器 › Top-down 管線歸因"));
            });
    }

    // ── 頻率真相 ────────────────────────────────────────────────────────────

    private static void AddFrequencyTruth(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_frequency_truth",
            "取得頻率的真實面貌：BCLK 基準時脈、TSC 頻率與晶振、原廠基頻與最低頻、超頻解鎖狀態、"
            + "各核心數對應的 Turbo 倍頻表、HWP 偏好核心，以及以 APERF／MPERF 實測的逐核有效時脈。"
            + "要回答「有沒有吃到 Turbo」「時脈是不是被鎖住」時用這個。此項需使用者先在 CPU 頁按量測。",
            _ =>
            {
                var s = vm.FreqTruth;
                if (Blank(s.BclkText) && s.TurboRows.Count == 0 && s.ClockRows.Count == 0)
                    return NotMeasured("頻率真相", "處理器 › 頻率真相");

                var sb = new StringBuilder();
                Line(sb, "BCLK", s.BclkText);
                Line(sb, "TSC", s.TscText);
                Line(sb, "原廠基頻", s.BaseText);
                Line(sb, "最低頻", s.MinText);
                Line(sb, "解鎖狀態", s.UnlockText);
                Line(sb, "HWP", s.HwpText);
                Line(sb, "交叉驗算", s.CrossCheckText);
                if (s.TurboRows.Count > 0)
                {
                    sb.AppendLine("Turbo 倍頻表（目前生效的設定，不是原廠規格——BIOS 改過就是改過的值）：");
                    Rows(sb, s.TurboRows, 16, r => $"{r.CoresText}：{r.RatioText}　{r.FreqText}"
                                                 + (string.IsNullOrWhiteSpace(r.Note) ? "" : $"（{r.Note}）"));
                }
                Line(sb, "倍頻表附註", s.TurboNote);
                if (s.ClockRows.Count > 0)
                {
                    sb.AppendLine("逐核有效時脈（視窗平均值，不是瞬時峰值）：");
                    Rows(sb, s.ClockRows, 12, r => $"{r.LpText}：{r.MhzText}（倍頻 {r.RatioText}）");
                }
                Line(sb, "說明", s.Status);
                return Done(sb, NotMeasured("頻率真相", "處理器 › 頻率真相"));
            });
    }

    // ── 平台可信度 ──────────────────────────────────────────────────────────

    private static void AddPlatformTrust(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_platform_trust",
            "取得平台可信度判定：這台機器上的 MSR／TSC／PMU 讀值是不是原生可信的。"
            + "會列出虛擬化旗標、Hypervisor 廠商、VBS／HVCI 狀態等事實。"
            + "**在轉述任何 MSR 類讀值（Top-down、頻率真相、機器檢查、免疫位元）之前先查這個**："
            + "若平台顯示讀值不可信，那些數字的解讀就得跟著打折。",
            _ =>
            {
                var s = vm.PlatformTrust;
                if (Blank(s.Verdict) && s.Rows.Count == 0)
                    return NotMeasured("平台可信度", "健康 › 平台可信度");

                var sb = new StringBuilder();
                Line(sb, "結論", s.Verdict);
                if (s.Rows.Count > 0)
                {
                    sb.AppendLine("讀到的事實：");
                    Rows(sb, s.Rows, 24, r => $"{r.Key}：{r.Value}"
                                            + (string.IsNullOrWhiteSpace(r.Note) ? "" : $"（{r.Note}）"));
                }
                sb.AppendLine("本項只回答「讀到的數字可不可信」，不下「你安全／不安全」的結論。");
                Line(sb, "說明", s.Status);
                return Done(sb, NotMeasured("平台可信度", "健康 › 平台可信度"));
            });
    }

    // ── BIOS 與 ME 韌體、微碼 ───────────────────────────────────────────────

    private static void AddFirmwareTruth(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_firmware_versions",
            "取得韌體版本事實：BIOS／UEFI 版本與日期、Intel ME（管理引擎）韌體版本（由 HECI 直接問韌體本身）、"
            + "以及處理器微碼版本（目前生效版本與 Windows 偏好版本並列）。"
            + "要回答「BIOS 該不該更新」「微碼有沒有吃到」時用這個。",
            _ =>
            {
                var s = vm.BiosMe;
                if (s.Bios.Count == 0 && s.Me.Count == 0 && s.Microcode.Count == 0)
                    return NotMeasured("BIOS 與 ME 韌體版本", "主機板 › BIOS 與 ME");

                var sb = new StringBuilder();
                if (s.Bios.Count > 0)
                {
                    sb.AppendLine("BIOS／UEFI：");
                    Rows(sb, s.Bios, 16, r => $"{r.Key}：{r.Value}");
                }
                if (s.Me.Count > 0)
                {
                    sb.AppendLine("Intel ME（管理引擎）：");
                    Rows(sb, s.Me, 16, r => $"{r.Key}：{r.Value}");
                }
                if (s.Microcode.Count > 0)
                {
                    sb.AppendLine("處理器微碼：");
                    Rows(sb, s.Microcode, 16, r => $"{r.Key}：{r.Value}");
                }
                sb.AppendLine("讀不到的項目就是讀不到，未以驅動版本或晶片組型號回推韌體版本。"
                    + "曦覽本身不寫入任何韌體：所謂「修改」只做兩件事——重開機進 UEFI 設定，或開啟原廠下載頁。");
                Line(sb, "說明", s.Status);
                return Done(sb, NotMeasured("BIOS 與 ME 韌體版本", "主機板 › BIOS 與 ME"));
            });
    }

    // ── 逐核時間歸因 ────────────────────────────────────────────────────────

    private static void AddCoreTime(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_core_time_breakdown",
            "取得逐核心的時間分佈（兩次取樣相減，約一秒視窗）：每顆邏輯處理器的忙碌率、"
            + "使用者模式／核心模式／DPC／中斷各占多少，以及每秒中斷次數。"
            + "要找出「哪一顆核被中斷或 DPC 吃掉」「負載是不是只壓在單核」時用這個。",
            _ =>
            {
                var s = vm.CoreTime;
                if (s.Rows.Count == 0) return NotMeasured("逐核時間歸因", "處理器 › 逐核時間歸因");

                var sb = new StringBuilder();
                Line(sb, "總結", s.Summary);
                Line(sb, "閒置週期", s.IdleCycles);
                sb.AppendLine("逐核（忙碌／使用者／核心／DPC／中斷／中斷率）：");
                Rows(sb, s.Rows, 12, r => $"{r.Name}：{r.BusyText}／{r.UserText}／{r.KernelText}"
                                        + $"／{r.DpcText}／{r.InterruptText}／{r.InterruptRateText}");
                sb.AppendLine("讀法（轉述時必須說明）：閒置時間本來就含在核心模式時間裡，DPC 與中斷又是核心時間的子集——"
                    + "這幾個數字不能相加當成 100%。閒置週期是 TSC 週期數，不是時間刻度，不要和百分比混用。");
                Line(sb, "說明", s.Status);
                if (!string.IsNullOrWhiteSpace(s.GroupNotice)) Line(sb, "處理器群組", s.GroupNotice);
                return Done(sb, NotMeasured("逐核時間歸因", "處理器 › 逐核時間歸因"));
            });
    }

    // ── 電源政策 ────────────────────────────────────────────────────────────

    private static void AddPowerPolicy(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_power_policy",
            "取得目前生效的電源政策：電源計劃名稱、處理器電源管理各項設定（最低／最高狀態、"
            + "核心停放、增強型 Turbo 等）與睡眠狀態支援情形。"
            + "要回答「是不是電源設定把效能壓住了」時用這個。本項只讀，不會改任何電源設定。",
            _ =>
            {
                var s = vm.PowerPolicy;
                if (s.Settings.Count == 0 && Blank(s.ProcessorSummary))
                    return NotMeasured("電源政策", "健康 › 電源政策");

                var sb = new StringBuilder();
                Line(sb, "電源計劃", s.PlanName);
                Line(sb, "處理器電源總結", s.ProcessorSummary);
                if (s.Settings.Count > 0)
                {
                    sb.AppendLine("處理器電源設定：");
                    Rows(sb, s.Settings, 24, r => $"{r.Name}：{r.Value}"
                                                + (string.IsNullOrWhiteSpace(r.Note) ? "" : $"（{r.Note}）"));
                }
                if (s.SleepStates.Count > 0)
                {
                    sb.AppendLine("睡眠狀態：");
                    Rows(sb, s.SleepStates, 12, r => $"{r.Name}：{r.Value}");
                }
                Line(sb, "關於「目前頻率」", s.CurrentMhzNotice);
                Line(sb, "適用範圍", s.ScopeNotice);
                Line(sb, "說明", s.Status);
                return Done(sb, NotMeasured("電源政策", "健康 › 電源政策"));
            });
    }

    // ── 記憶體真實面貌 ──────────────────────────────────────────────────────

    private static void AddMemoryTruth(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_memory_commit_truth",
            "取得記憶體的認可（commit）帳本：目前認可量與認可上限、開機至今的認可尖峰、分頁大小，"
            + "以及「尖峰是否曾超過實體記憶體」的判定。"
            + "要回答「記憶體到底夠不夠、有沒有被逼去用分頁檔」時用這個——"
            + "注意它量的是認可帳本，不是真的寫出去多少頁面。",
            _ =>
            {
                var s = vm.MemoryTruth;
                if (Blank(s.CommitText) && Blank(s.Verdict))
                    return NotMeasured("記憶體真實面貌", "記憶體 › 真實面貌");

                var sb = new StringBuilder();
                Line(sb, "目前認可量", s.CommitText);
                Line(sb, "認可尖峰（開機至今累計最大值，不是現在的狀態）", s.PeakText);
                Line(sb, "分頁大小", s.PageSizeText);
                Line(sb, "判定", s.Verdict);
                Line(sb, "判定說明", s.VerdictText);
                sb.AppendLine("這是認可帳本，不等於「實際寫出了多少頁面到磁碟」——本項不宣稱後者。");
                return Done(sb, NotMeasured("記憶體真實面貌", "記憶體 › 真實面貌"));
            });
    }

    // ── 機器檢查（MCA）與 WHEA ─────────────────────────────────────────────

    private static void AddMachineCheck(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_machine_check",
            "取得硬體錯誤的兩份證據：一是機器檢查架構（MCA）各 bank 的當下計數器讀值，"
            + "二是 WHEA 事件記錄中的歷史硬體錯誤。"
            + "要回答「這台機器有沒有記憶體／快取／匯流排層級的硬體錯誤」時用這個。0 筆是好事，就照實說是 0 筆。",
            _ =>
            {
                var sb = new StringBuilder();
                var mca = vm.Mca;
                if (mca.Rows.Count == 0 && Blank(mca.Summary))
                    sb.AppendLine("MCA 各 bank：" + NotMeasured("機器檢查計數器", "健康 › 機器檢查"));
                else
                {
                    Line(sb, "MCA 總結", mca.Summary);
                    if (mca.Rows.Count > 0)
                    {
                        sb.AppendLine("MCA 各 bank（核心／bank／類型／已修正次數）：");
                        Rows(sb, mca.Rows, 20, r => $"{r.Core}／{r.Bank}／{r.Kind}／{r.Corrected}"
                                                  + (string.IsNullOrWhiteSpace(r.Detail) ? "" : $"　{r.Detail}"));
                    }
                    sb.AppendLine("這是「當下計數器讀值」，不是歷史紀錄；歷史看下面的 WHEA。");
                    Line(sb, "MCA 說明", mca.Status);
                }

                var whea = vm.Whea;
                if (whea.Rows.Count == 0 && Blank(whea.Summary))
                    sb.AppendLine("WHEA 事件：" + NotMeasured("WHEA 硬體錯誤事件", "健康 › WHEA 硬體錯誤"));
                else
                {
                    Line(sb, "WHEA 總結", whea.Summary);
                    if (whea.Rows.Count > 0)
                    {
                        sb.AppendLine("WHEA 事件（時間／等級／ID／訊息）：");
                        Rows(sb, whea.Rows, 15, r => $"{r.Time}／{r.Level}／{r.Id}／{r.Message}");
                    }
                }
                return Done(sb, "（機器檢查與 WHEA 皆尚未讀取。）");
            });
    }

    // ── 處理器免疫位元 ──────────────────────────────────────────────────────

    private static void AddSecurityBits(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_cpu_security_bits",
            "取得處理器與微碼宣告的推測執行免疫位元（IA32_ARCH_CAPABILITIES 各位元、"
            + "IBRS／STIBP／SSBD 現況）。這是位元事實，不是「你有沒有中鏢」的結論。",
            _ =>
            {
                var s = vm.CpuSecurity;
                if (s.Rows.Count == 0) return NotMeasured("處理器免疫位元", "處理器 › 免疫位元");

                var sb = new StringBuilder();
                sb.AppendLine("免疫位元讀值：");
                Rows(sb, s.Rows, 28, r => $"{r.Key}：{r.Value}");
                sb.AppendLine("只呈現位元事實，不宣稱使用者受不受某個 CVE 影響。"
                    + "若微碼未宣告任何免疫位元（值為 0），那代表這顆晶片或這版微碼沒有提供免疫聲明，"
                    + "而不是「已經中鏢」——轉述時不要把兩者混為一談。");
                Line(sb, "說明", s.Status);
                return Done(sb, NotMeasured("處理器免疫位元", "處理器 › 免疫位元"));
            });
    }

    // ── RDT 快取占用與記憶體頻寬 ────────────────────────────────────────────

    private static void AddRdt(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_rdt_monitoring",
            "取得 Intel RDT 的快取占用（CMT）與記憶體頻寬（MBM）監測讀值：全系統總頻寬與逐核占用。"
            + "本機平台若未開放 RDT 監測，讀值會恆為 0——那是誠實的結果，不是量到 0。",
            _ =>
            {
                var s = vm.Rdt;
                var sb = new StringBuilder();
                Line(sb, "RDT 支援", s.SupportText);
                if (s.Rows.Count == 0)
                    return Done(sb, "") + "\n" + NotMeasured("RDT 快取／頻寬監測", "處理器 › RDT 監測");

                Line(sb, "全系統總頻寬", s.TotalBwText);
                sb.AppendLine("逐核（快取占用／總頻寬／本地頻寬）：");
                Rows(sb, s.Rows, 12, r => $"LP{r.Lp}：{r.OccupancyText}／{r.TotalBwText}／{r.LocalBwText}");
                sb.AppendLine("限制（轉述時必須說明）：使用者模式下無法做行程層級歸因，只有全系統與逐核；"
                    + "MBM 計數器會繞回，該秒的負差值直接捨棄；"
                    + "若三項讀值全為 0 且無錯誤旗標，那是平台／韌體未開放 RDT 監測的樣子，如實顯示 0，不用估計值頂替。");
                Line(sb, "說明", s.Status);
                return Done(sb, NotMeasured("RDT 快取／頻寬監測", "處理器 › RDT 監測"));
            });
    }
}
