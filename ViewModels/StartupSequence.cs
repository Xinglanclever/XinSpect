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

            // 天梯榜：以處理器名稱標示本機名次（顯示卡名稱待感測器就緒後由脈動補標）
            try { vm.Ranking.Highlight(vm.Cpu.Name, vm.Live?.PrimaryGpu?.Name); } catch { /* 天梯高亮為附加功能 */ }
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
            vm.SpdModules = report.Spd;
            vm.GpuDetails = report.Gpus;

            if (vm.Live is not null)
                vm.StatusText = ReadyText(vm, report.Ran);
        }
        catch { /* 深度規格為附加，讀取失敗維持 WMI 值 */ }
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
