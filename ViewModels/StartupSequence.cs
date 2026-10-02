namespace XinSpect;

/// <summary>
/// 開機偵測序列：把「哪些資訊在什麼時候、以什麼順序被讀進來」集中於一處。
/// </summary>
/// <remarks>
/// 每一步都獨立 try/catch 並向下降級：任一硬體介面缺失（WMI 停用、無 Ring0 驅動、無 NVIDIA 卡…）
/// 都只讓對應的欄位維持預設值，絕不中斷後續步驟或終結應用程式。
/// <see cref="ReinitializeAsync"/> 是同一組步驟的重跑版本，差別在於不重建每秒脈動。
///
/// 順序上只保留<b>真實的相依</b>：磁碟資訊要等感測器就緒才能套用，其餘（WMI 靜態資訊、感測器引擎、
/// 網路引擎、CUDA 版本、螢幕色域）彼此無關，故一併起跑，讓幾段各自數百毫秒的等待重疊而非相加。
/// </remarks>
internal static class StartupSequence
{
    private static readonly SemaphoreSlim DeepSpecsGate = new(1, 1);
    /// <summary>開機序列：讀靜態資訊 → 起感測與網路引擎 → 補齊磁碟／色域／CUDA → 起脈動 → 背景深度規格與自檢。</summary>
    public static async Task RunAsync(MainViewModel vm, MetricsPump pump)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 0) 已存設定：啟動時的預設紀年
        vm.ApplySavedEra();

        // 0.1) 工具箱插槽：回填已保存的本機執行檔路徑（工具箱先於設定建立，故於此接上設定服務）
        try { vm.Toolbox.AttachSettings(vm.Settings); } catch { /* 插槽為附加功能 */ }

        // 1) 彼此獨立的慢動作一起起跑（每一個都會在下方被 await，不會變成沒人接的 Task）
        vm.StatusText = "正在讀取系統與硬體資訊…";
        var sensorTask = Task.Run(() => new SensorService());
        var netTask = Task.Run(() => new NetworkService());
        var cudaTask = Task.Run(CudaService.DetectVersion);
        var edidTask = Task.Run(EdidService.Detect);

        await LoadStaticInfoAsync(vm);

        // 2) 感測器（LHM，需載入 Ring0 驅動）
        vm.StatusText = "正在啟動感測器引擎…";
        try { vm.Live = await sensorTask; }
        catch (Exception ex) { vm.StatusText = "感測器初始化失敗（溫度/頻率不可用）：" + ex.Message; }

        // 3) 網路監控
        try { vm.Net = await netTask; } catch { /* 網路資訊為附加功能 */ }

        // 3.1) 核心溫度熱力圖（需感測器＋拓樸）
        if (vm.Live is not null)
            try { vm.CoreTempMap = new CoreTempMapService(vm.Live, vm.CpuTopology); }
            catch { /* 熱力圖為附加功能 */ }

        // 3.5 / 3.6) 磁碟容量與類型（WMI 較慢：背景查詢後就地套用），並建立可切換的活動走勢檢視
        if (vm.Live is not null)
        {
            await ApplyDiskInfoAsync(vm);
            try { vm.SetupDiskActivityView(); } catch { /* 磁碟活動走勢為附加功能 */ }
            try { vm.FanCurves.Attach(vm.Live.FanControls); } catch { /* 無可控風扇則曲線頁自行顯示空狀態 */ }
        }

        // 3.7–3.9) CUDA 版本、磁碟效能清單、螢幕色域（前兩者已於步驟 1 起跑，此處僅收成果）
        await LoadSecondaryInfoAsync(vm, cudaTask, edidTask);

        // 3.95 / 3.96) 超頻與顯示卡超頻引擎（未就緒時降級為唯讀監測）
        StartOcEngines(vm);

        // 4) 每秒脈動（間隔由設定決定，變更即時套用）
        pump.Start();
        vm.UpdateClock();
        vm.StartupSeconds = sw.Elapsed.TotalSeconds;      // 實測值，不是估計值
        if (vm.Live is not null) vm.StatusText = ReadyText(vm);

        // 5) 深度規格（背景呼叫 CPU-Z 產生報告，約需 10 餘秒）＋ WinSAT 快取分數
        _ = LoadDeepSpecsAsync(vm);
        _ = vm.Winsat.LoadCachedAsync();

        // 6) 首次啟動的環境自檢（待各引擎稍稍就緒後於背景執行一次）
        _ = RunStartupEnvCheckAsync(vm);

        // 7) 藍屏傾印併入事件時間軸（掃描 %SystemRoot%\Minidump，以檔名去重可重複呼叫）
        _ = ImportBsodAsync(vm);

        // 8) 場景頁的「目前電源計劃」（powercfg 子行程，置於背景）
        _ = vm.Profiles.RefreshPowerPlanAsync();

        // 9) 藍色中隊：唯讀安全評估 + BlueSquadronBridge 守護進程（模組內建一次性保護）
        try { _ = vm.BlueSquadron.InitializeAsync(vm); } catch { /* 安全防護為附加功能 */ }
    }

    // 就緒狀態列。啟動耗時只在真的量到時才附上；深度規格讀到了也一併說明。
    private static string ReadyText(MainViewModel vm, bool deepSpecs = false)
    {
        string s = "就緒 ・ 每秒更新中";
        if (deepSpecs) s += " ・ 深度規格已讀取";
        if (vm.StartupText.Length > 0) s += " ・ " + vm.StartupText;
        return s;
    }

    /// <summary>設定頁「所有功能一鍵初始化」：重跑各模組偵測，但不重建每秒脈動。</summary>
    public static async Task ReinitializeAsync(MainViewModel vm)
    {
        vm.StatusText = "正在重新初始化所有功能…";

        await LoadStaticInfoAsync(vm);

        // 感測器 / 網路引擎：先前失敗者重試建立
        if (vm.Live is null) { try { vm.Live = await Task.Run(() => new SensorService()); } catch { /* 感測器不可用 */ } }
        if (vm.Net is null) { try { vm.Net = await Task.Run(() => new NetworkService()); } catch { /* 網路資訊為附加 */ } }

        if (vm.Live is not null) await ApplyDiskInfoAsync(vm);
        try { if (vm.Live is not null) vm.FanCurves.Attach(vm.Live.FanControls); } catch { /* 無可控風扇 */ }
        await LoadSecondaryInfoAsync(vm);
        StartOcEngines(vm);

        // 一鍵裝機：重新偵測 winget 是否可用
        try { await vm.Winget.DetectAsync(); } catch { /* winget 偵測為附加 */ }

        _ = LoadDeepSpecsAsync(vm);
        _ = vm.Winsat.LoadCachedAsync();
        _ = vm.Profiles.RefreshPowerPlanAsync();

        vm.StatusText = vm.Live is null ? "重新初始化完成（部分模組不可用）" : "重新初始化完成 ・ 每秒更新中";
    }

    // ===== 各階段 =====

    // WMI 靜態資訊：系統摘要、處理器、記憶體模組、音效卡、網路卡、拓樸，並以主機板值先填主機板頁。
    private static async Task LoadStaticInfoAsync(MainViewModel vm)
    {
        try
        {
            var (summary, cpu, modules, sound, nics) = await Task.Run(() =>
                (SystemInfoService.GetSystemSummary(),
                 SystemInfoService.GetCpu(),
                 SystemInfoService.GetMemoryModules(),
                 SystemInfoService.GetSoundDevices(),
                 SystemInfoService.GetNetworkAdapters()));

            vm.System = summary;
            vm.Cpu = cpu;
            vm.CpuTopology = await Task.Run(CpuTopologyService.Build);
            vm.Modules.Clear();
            foreach (var m in modules) vm.Modules.Add(m);
            vm.SoundDevices = sound;
            vm.InstalledNics = nics;

            // 主機板分頁先以 WMI 廠商/型號填入，稍後 CPU-Z 報告再補全晶片組/BIOS 等深度欄位
            vm.Mainboard = new MainboardDetail { Vendor = summary.BoardVendor, Model = summary.BoardModel };
        }
        catch (Exception ex)
        {
            vm.StatusText = "系統靜態資訊讀取失敗（WMI 不可用）：" + ex.Message;
        }
    }

    // 磁碟容量 / 類型 / HDD 健康：背景 WMI 查詢後套用到既有磁碟列。
    private static async Task ApplyDiskInfoAsync(MainViewModel vm)
    {
        try
        {
            var disks = await Task.Run(DiskInfoService.Query);
            vm.Live?.ApplyDiskInfo(disks);
            vm.PhysicalDisks = disks;
        }
        catch { /* 磁碟靜態資訊為附加，失敗則容量/類型維持預設 */ }
    }

    // CUDA 版本、可測試磁碟清單、螢幕色域（皆為附加資訊）。
    // 開機序列會把 CUDA 與色域先在步驟 1 起跑後傳入；重新初始化時則現場查詢。
    private static async Task LoadSecondaryInfoAsync(
        MainViewModel vm, Task<string?>? cuda = null, Task<List<MonitorGamutInfo>>? edid = null)
    {
        try { vm.CudaVersion = await (cuda ?? Task.Run(CudaService.DetectVersion)) ?? "****"; }
        catch { vm.CudaVersion = "****"; }

        try { vm.DiskBench.PopulateDrives(); } catch { /* 磁碟清單為附加功能 */ }
        try { vm.Monitors = await (edid ?? Task.Run(EdidService.Detect)); } catch { /* 色域為附加功能 */ }
    }

    // 超頻 / 顯示卡超頻引擎：射後不理，未就緒者自行降級為唯讀監測。
    private static void StartOcEngines(MainViewModel vm)
    {
        try { _ = vm.Overclock.InitializeAsync(); } catch { /* 超頻為測試版附加功能 */ }
        try { _ = vm.GpuOc.InitializeAsync(); } catch { /* 顯示卡超頻為測試版附加功能 */ }
    }

    // 深度規格：CPU-Z 子行程報告（時序、SPD、主機板、顯示卡）。以射後不理呼叫，故整段包覆。
    private static async Task LoadDeepSpecsAsync(MainViewModel vm)
    {
        if (!await DeepSpecsGate.WaitAsync(0)) return;
        try { await LoadDeepSpecsCore(vm); }
        finally { DeepSpecsGate.Release(); }
    }

    private static async Task LoadDeepSpecsCore(MainViewModel vm)
    {
        // SPD 先自己讀。讀得到就用它，因為那是模組上的原始位元組（來源那一列會標明是哪一條匯流排）；
        // 讀不到就保留既有值，讓下面的 CPU-Z 報告照原樣填——在讀不到 SPD 的機器上完全沒有回歸。
        var nativeSpd = await Task.Run(ReadSpdDirect);
        vm.DirectSpdReads = nativeSpd;
        if (nativeSpd.Count > 0) vm.SpdModules = SpdDisplay.ToDisplay(nativeSpd);

        // 晶片組安全暫存器（BIOS_CNTL/SMRAMC）：走 WinRing0 讀 bus 0；讀不到由三態如實標示（缺 ring0 / 非 Intel）。
        // SPI 快閃安全同場載入：SPIBAR 經 PCI 取得，暫存器本體要 MMIO——自家驅動未載時整組三態（不在生產機自動觸發核心碼）。
        try
        {
            using var pci = new WinRing0PciConfigReader();
            vm.EvidenceLab.LoadChipsetSecurity(pci);
            vm.EvidenceLab.LoadSpiFlash(pci, new NotLoadedMmioReader());
        }
        catch { /* 晶片組安全為附加功能，讀不到由三態標示 */ }

        // ACPI 表清單（usermode 列舉，不需驅動；BERT/HEST/SRAT/DMAR 的有無即是平台能力的指紋）。
        try { vm.EvidenceLab.LoadAcpi(new Win32AcpiTableSource()); }
        catch { /* ACPI 列舉為附加功能，讀不到由三態標示 */ }

        // PCIe AER（ECAM 基底 usermode 可得；擴充組態空間要 MMIO——自家驅動未載時三態）。
        try { vm.EvidenceLab.LoadPcieAer(new NotLoadedMmioReader(), new Win32AcpiTableSource()); }
        catch { /* PCIe AER 為附加功能，讀不到由三態標示 */ }

        try
        {
            var report = await CpuzReportService.ReadAsync();

            // 時序（含次要時序），沿用既有繫結物件
            report.Timings.RaiseAll();
            vm.Timings = report.Timings;

            // 主機板廠商 CPU-Z 常只給代碼，以 WMI 值補入後再指派（讓 Brand 解析正確）
            if (report.Board.Vendor == "—" && vm.System.BoardVendor != "—")
                report.Board.Vendor = vm.System.BoardVendor;
            if (report.Board.Model == "—" && vm.System.BoardModel != "—")
                report.Board.Model = vm.System.BoardModel;

            vm.CpuDetail = report.Cpu;
            vm.Mainboard = report.Board;
            vm.CpuzSpdModules = report.Spd;
            if (nativeSpd.Count == 0) vm.SpdModules = report.Spd;
            vm.GpuDetails = report.Gpus;

            if (vm.Live is not null)
                vm.StatusText = ReadyText(vm, report.Ran);
        }
        catch { /* 深度規格為附加，讀取失敗維持 WMI 值 */ }

        // GPU 硬解／編碼能力矩陣：D3D11 VideoDevice 列舉 + MFT 硬體編碼器（零特權、純 API 直讀）
        try { vm.GpuCodecs = GpuCodecService.Probe().Adapters; }
        catch { /* 編解碼偵測為附加功能 */ }

        // 核心記憶體池細目（RAMMap 式）：Paged/Nonpaged/Standby 各優先級，零特權直讀
        try
        {
            var pool = MemoryPoolService.Read();
            vm.PoolSnapshot = pool;
            if (pool is not null)
                for (int p = 0; p < pool.StandbyByPriorityMB.Length; p++)
                    vm.PoolStandbyLabels.Add(MemoryPoolService.StandbyLabel(p, pool.StandbyByPriorityMB[p]));
        }
        catch { /* 池細目為附加功能 */ }

        // CPU-Z 沒有（未安裝／不在桌面）不代表時脈就只能空著：
        // SPD 直讀結果（上面已讀）與 WMI 設定速率都能把「時脈與時序」補起來，
        // CPU-Z 從「必要依賴」降級為「更完整的選用補充」。
        if (!vm.Timings.Loaded)
        {
            var native = await Task.Run(() => BuildNativeTimings(vm));
            if (native is not null)
            {
                native.RaiseAll();
                vm.Timings = native;
            }
            else
            {
                vm.Timings.Status = "未讀到時序——需要 SPD 直讀（管理員＋驅動）或 CPU-Z 報告其中之一。";
                vm.Timings.SourceText = "無";
                vm.Timings.RaiseAll();
            }
        }
    }

    /// <summary>
    /// 不靠 CPU-Z 組出一組記憶體時脈資訊。
    /// 優先序：SPD 直讀（模組原始位元組，含 JEDEC 時序）→ WMI 設定速率（只有頻率，沒有時序）。
    /// 讀不到任何東西回 null，呼叫端如實顯示「未讀到」。純函式（輸入皆為已就緒的唯讀資料）供單元測試。
    /// </summary>
    internal static MemoryTimings? BuildNativeTimings(MainViewModel vm)
    {
        // ── 來源一：SPD 直讀（DDR4；時序以 JEDEC 標準值為準，XMP 開啟時實際值可能更緊）──
        var spd = vm.DirectSpdReads.FirstOrDefault();
        if (spd is { } first)
        {
            var t = first.Decoded.Timings;
            if (t.TckMinPs > 0)
            {
                int cl = SpdTimings.ClocksAt(t.TaaPs, t.TckMinPs);
                int dataRate = t.MaxJedecDataRate;
                return new MemoryTimings
                {
                    Loaded = true,
                    SourceText = "SPD 直讀（" + first.Bus + "）・JEDEC 標準值",
                    Status = "已由 SPD 直讀填入。注意：這是模組的 JEDEC 標準時序；"
                           + "若 BIOS 已開 XMP／EXPO，實際時序會比這裡更緊——完整對照仍以 CPU-Z 報告或 BIOS 為準。",
                    MemoryTypeText = "DDR4",
                    DataRateText = dataRate > 0 ? $"DDR4-{dataRate}" : "—",
                    DramFrequencyMHz = dataRate > 0 ? dataRate / 2.0 : 0,
                    CL = cl > 0 ? cl.ToString() : "—",
                    TRCD = SpdTimings.ClocksAt(t.TrcdPs, t.TckMinPs).ToString(),
                    TRP = SpdTimings.ClocksAt(t.TrpPs, t.TckMinPs).ToString(),
                    TRAS = SpdTimings.ClocksAt(t.TrasPs, t.TckMinPs).ToString(),
                    TRFC = t.Trfc1Ps > 0 ? SpdTimings.ClocksAt(t.Trfc1Ps, t.TckMinPs).ToString() : "—",
                    MemorySizeText = vm.DirectSpdReads.Count > 0
                        ? $"{vm.DirectSpdReads.Sum(r => r.Decoded.Geometry.CapacityMib) / 1024.0:0.#} GB（{vm.DirectSpdReads.Count} 條）"
                        : "—",
                };
            }
        }

        // ── 來源二：WMI（Win32_PhysicalMemory 的 ConfiguredClockSpeed＝目前設定的資料速率）──
        var mods = vm.Modules;
        if (mods is { Count: > 0 } && mods[0].ConfiguredSpeedMHz > 0)
        {
            int configured = mods[0].ConfiguredSpeedMHz;
            double totalGb = mods.Sum(m => m.CapacityGB);
            string type = mods[0].MemoryType.Length > 0 ? mods[0].MemoryType : "DDR";
            return new MemoryTimings
            {
                Loaded = false,   // 時序欄位沒有來源，不假裝有
                SourceText = "Windows 設定速率（WMI）",
                Status = "已由 Windows（WMI）取得目前設定的資料速率；時序（CL／tRCD 等）WMI 不提供，"
                       + "需 SPD 直讀（以系統管理員執行）或 CPU-Z 報告才讀得到。",
                MemoryTypeText = type,
                DataRateText = $"{type}-{configured}（WMI 設定值）",
                DramFrequencyMHz = configured / 2.0,   // DDR：資料速率 ÷ 2 ＝記憶體時鐘
                MemorySizeText = totalGb > 0 ? $"{totalGb:0.#} GB（{mods.Count} 條）" : "—",
            };
        }

        return null;
    }

    /// <summary>
    /// 走 SMBus 把每條記憶體模組的 SPD 直接讀回來（背景執行緒）。任何一關過不了就回空清單。
    /// </summary>
    /// <remarks>
    /// 三道讓路：沒有驅動或沒有管理員權限就不讀；具名互斥鎖取不到（CPU-Z／AIDA64／燈光軟體
    /// 正在用匯流排）就不搶；每一條匯流排的硬體旗號取不到也各自跳過。
    /// 回空清單不是錯誤，只是這台機器這一刻讀不到——呼叫端會退回 CPU-Z 報告那條路徑。
    /// </remarks>
    private static List<SpdDirectRead> ReadSpdDirect()
    {
        try
        {
            using var bridge = WinRing0Bridge.Create();
            if (!bridge.Available) return [];

            var notes = new List<string>();
            var buses = SpdBusFactory.Candidates(bridge, notes);
            if (buses.Count == 0) return [];

            using var busLock = SmbusBusLock.TryAcquire(SmbusBusLock.WellKnownName, 500, out _);
            if (busLock is null) return [];

            return SpdSurveyor.Survey(buses).Modules.ToList();
        }
        catch { return []; }
    }

    // 首次啟動的環境自檢：略候片刻讓各引擎與感測器就緒，再於背景跑一次。
    private static async Task RunStartupEnvCheckAsync(MainViewModel vm)
    {
        try
        {
            await Task.Delay(2500);
            if (!vm.EnvCheck.HasRun && !vm.EnvCheck.IsRunning) await vm.EnvCheck.RunAsync(vm);
        }
        catch { /* 環境自檢為附加功能，失敗不影響其餘功能 */ }
    }

    // 藍屏傾印：掃描為 I/O 動作，置於背景；併入事件時間軸後由時間軸自行去重與持久化。
    private static async Task ImportBsodAsync(MainViewModel vm)
    {
        try
        {
            var bsod = new BsodService();
            await Task.Run(bsod.Scan);
            vm.Events.ImportBsod(bsod);
        }
        catch { /* 傾印匯入為附加功能 */ }
    }
}
