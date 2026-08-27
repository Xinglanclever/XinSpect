using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace XtuBridge
{
    // ───────────────────────────────────────────────────────────────────────
    // XtuBridge 的核心：在「原生 .NET Framework 4.8」執行階段以反射呼叫 Intel XTU
    // 的 IntelOverclockingSDK.dll。之所以獨立成一支 exe，是因為該 SDK 建構時需要
    // 舊版 WCF（System.ServiceModel 4.0.0.0），而 .NET 10 已移除該組件 ——
    // 於是把 SDK 放回它原生的 Framework 環境跑，再以 JSON 管線把結果交給 .NET 10 本體。
    //
    // 本檔僅回傳「純資料」（KnobData 等），不含任何 WPF / XinSpect 專屬型別；
    // 旋鈕分類、格式化、容差判定全部留在 .NET 10 本體，讓橋接程式保持精簡。
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>單一可調控制項的原始資料（分類與格式化由本體端負責）。</summary>
    public sealed class KnobData
    {
        public uint Id;
        public string Name = "";
        public string Category = "";
        public string Unit = "";
        public double Min, Max, Default, Boot, Active;
        public bool RealTime, RequiresReboot, ReadOnly, Enabled;

        public bool Writable => Enabled && !ReadOnly && Max > Min;
    }

    /// <summary>初始化結果：狀態 + 能力 + 看門狗 + 列舉到的旋鈕。</summary>
    public sealed class InitResult
    {
        public string Status = "NotInitialized"; // Ready / Unsupported / Missing / Failed / NotInitialized
        public string Message = "";
        public int Family;
        public bool CoreTunable, BclkTunable, MemoryTunable, CacheTunable, SpeedOptimizerSupported;
        public bool WatchdogPresent, WatchdogRunning, WatchdogFailed;
        public List<KnobData> Knobs = new List<KnobData>();
    }

    /// <summary>單一寫入的硬體回饋（容差 / 是否生效的語意判定留給本體端）。</summary>
    public sealed class ApplyOut
    {
        public bool Ok;
        public string Code;      // SDK 回報碼（GeneralCode），可能為 null
        public bool ActiveKnown;
        public double Active;
    }

    public sealed class XtuCore : IDisposable
    {
        private readonly object _lock = new object();
        private readonly List<KnobData> _knobs = new List<KnobData>();

        private Assembly _sdkAsm;
        private object _facade;      // IntelOverclockingLibrary
        private object _tuning;      // ITuningLibrary
        private Type _tuningType;

        private MethodInfo _mTune, _mApply, _mRefresh, _mGetControl, _mDiscard;
        private MethodInfo _mGetAvailable, _mRescan;   // ITuningLibrary 上的顯式介面實作，須經介面型別解析
        private Type _tuningIface;
        private ResolveEventHandler _resolver;
        private readonly List<string> _probeDirs = new List<string>();

        public string Status = "NotInitialized";
        public string Message = "尚未初始化";

        // ── 診斷紀錄（stdout 已被 JSON 協定佔用，錯誤另寫檔）───────────────────
        public static string RootDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect", "Overclock");

        public static void Diag(string msg)
        {
            try
            {
                Directory.CreateDirectory(RootDir);
                File.AppendAllText(Path.Combine(RootDir, "bridge.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine + Environment.NewLine);
            }
            catch { /* 診斷失敗不影響主流程 */ }
        }

        // ── 初始化 ───────────────────────────────────────────────────────────
        public InitResult Initialize()
        {
            var res = new InitResult();
            try
            {
                string dll = LocateSdk();
                if (dll == null)
                {
                    Status = "Missing";
                    Message = "找不到 Intel XTU SDK（IntelOverclockingSDK.dll）。請安裝 Intel Extreme Tuning Utility，"
                            + "或在 " + Path.Combine(RootDir, "xtu_path.txt") + " 內填入其 Client 目錄路徑。";
                    return Fill(res);
                }

                // 依賴（XtuCommon.dll / IronCity.* / 驅動組件）從 Client 與 Drivers 目錄解析
                _resolver = delegate (object s, ResolveEventArgs e)
                {
                    string want = new AssemblyName(e.Name).Name + ".dll";
                    foreach (var dir in _probeDirs)
                    {
                        string f = Path.Combine(dir, want);
                        if (File.Exists(f))
                        {
                            try { return Assembly.LoadFrom(f); } catch { /* 換下一個目錄 */ }
                        }
                    }
                    return null;
                };
                AppDomain.CurrentDomain.AssemblyResolve += _resolver;

                _sdkAsm = Assembly.LoadFrom(dll);
                Diag("已載入 SDK：" + dll + "（" + _sdkAsm.FullName + "）");

                Type facadeType = _sdkAsm.GetType("Intel.Overclocking.SDK.IntelOverclockingLibrary");
                if (facadeType == null)
                    facadeType = _sdkAsm.GetTypes().FirstOrDefault(t => t.Name == "IntelOverclockingLibrary");
                if (facadeType == null)
                {
                    Status = "Failed";
                    Message = "SDK 已載入，但找不到 IntelOverclockingLibrary 型別（版本不相容？）。";
                    return Fill(res);
                }

                _facade = Activator.CreateInstance(facadeType);
                Diag("已建立 IntelOverclockingLibrary 實例");
                try { Call(_facade, "Initialize"); } catch { /* 某些版本免顯式初始化 */ }

                bool? compat = null;
                try { compat = Call(_facade, "PlatformCompatibilityCheck") as bool?; } catch { }

                _tuning = Prop(_facade, "TuningLib");
                _tuningType = _tuning == null ? null : _tuning.GetType();
                // GetAvailableControls / GetControl / RescanAvailableControls 為顯式介面實作，
                // 只在 ITuningLibrary 介面型別上可見（在具象型別上是私有的）——故保留介面型別。
                _tuningIface = _sdkAsm.GetType("Intel.Overclocking.SDK.Tuning.ITuningLibrary");
                if (_tuning == null || _tuningType == null)
                {
                    Status = "Unsupported";
                    Message = "SDK 已載入，但無法取得 TuningLib（此平台可能不支援超頻）。";
                    return Fill(res);
                }

                CacheMethods();
                ProbeCapabilities(res);
                ProbeWatchdog(res);
                EnumerateControls();

                if (_knobs.Count == 0)
                {
                    Status = "Unsupported";
                    Message = "已連接 Intel XTU SDK，但此平台 / BIOS 未開放任何可調項（超頻可能被鎖定）。"
                            + (compat == false ? "（PlatformCompatibilityCheck 回報不相容）" : "");
                    return Fill(res);
                }

                int writable = _knobs.Count(k => k.Writable);
                Status = "Ready";
                Message = "已連接 Intel XTU SDK ・ 處理器家族 " + res.Family + " ・ 可調項 " + _knobs.Count + " 個"
                        + "（可寫入 " + writable + " 個）";
                return Fill(res);
            }
            catch (Exception ex)
            {
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;
                Status = "Failed";
                Message = "超頻引擎初始化失敗：" + root.Message + "（" + root.GetType().Name + "）";
                Diag("初始化擲出例外：\n" + ex);
                return Fill(res);
            }
        }

        private InitResult Fill(InitResult res)
        {
            res.Status = Status;
            res.Message = Message;
            res.Knobs = _knobs;
            return res;
        }

        private void CacheMethods()
        {
            Type t = _tuningType;
            _mTune = FindMethod(t, "Tune", typeof(uint), typeof(decimal), typeof(bool));
            if (_mTune == null)
                _mTune = t.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m =>
                {
                    var ps = m.GetParameters();
                    return m.Name == "Tune" && ps.Length == 3
                        && ps[1].ParameterType == typeof(decimal) && !ps[1].ParameterType.IsByRef;
                });
            _mApply = FindMethod(t, "ApplyChanges", typeof(bool));
            _mRefresh = FindMethod(t, "RefreshActiveValuesFromHardwareAccess", typeof(bool));
            _mDiscard = FindMethod(t, "DiscardChanges");

            // 這三個是 ITuningLibrary 上的顯式介面實作，須從介面型別取得 MethodInfo，
            // 再以實例（_tuning 實作了該介面）叫用 —— 這是先前 KNOBS=0 的真正原因。
            Type it = _tuningIface;
            _mGetControl = (it != null ? it.GetMethod("GetControl", new[] { typeof(uint) }) : null)
                           ?? FindMethod(t, "GetControl", typeof(uint));
            _mGetAvailable = it != null ? it.GetMethod("GetAvailableControls", Type.EmptyTypes) : null;
            _mRescan = it != null ? it.GetMethod("RescanAvailableControls", Type.EmptyTypes) : null;
        }

        private void ProbeCapabilities(InitResult res)
        {
            res.CoreTunable = TryBool("IsProcessorCoreTunable");
            res.BclkTunable = TryBool("IsBclkTunable");
            res.MemoryTunable = TryBool("IsMemoryTunable");
            res.CacheTunable = TryBool("IsProcessorRingCacheTunable");
            res.SpeedOptimizerSupported = TryBool("IsIntelSpeedOptimizerSupported");
            try { res.Family = Convert.ToInt32(Call(_tuning, "GetProcessorFamily") ?? 0); } catch { }
        }

        private void ProbeWatchdog(InitResult res)
        {
            try
            {
                object sysinfo = Prop(_facade, "SystemInfoLib");
                object wd = FindWatchdog(sysinfo);
                if (wd != null)
                {
                    res.WatchdogPresent = Call(wd, "IsWatchdogTimerPresent") as bool? ?? false;
                    res.WatchdogRunning = Call(wd, "IsWatchdogTimerRunning") as bool? ?? false;
                    res.WatchdogFailed = Call(wd, "HasWatchdogTimerFailed") as bool? ?? false;
                }
            }
            catch { /* 看門狗狀態為附加資訊，取不到不影響主功能 */ }
        }

        private void EnumerateControls()
        {
            _knobs.Clear();
            var list = (_mGetAvailable != null ? _mGetAvailable.Invoke(_tuning, null) : null) as IEnumerable;
            if (list == null) return;
            foreach (var ctrl in list)
            {
                var k = BuildKnob(ctrl);
                if (k != null) _knobs.Add(k);
            }
        }

        private KnobData BuildKnob(object ctrl)
        {
            if (ctrl == null) return null;
            try
            {
                string ctype = (Prop(ctrl, "ControlType") == null ? "" : Prop(ctrl, "ControlType").ToString()).ToLowerInvariant();
                // 只保留「數值區間（Range）」旋鈕（滑桿）；開關(Toggle) / 唯讀 / 時序表格 / XMP 等一律跳過。
                // 注意：本版 SDK 的開關型別字串為 "Toggle"（非舊版 "OnOff"），故採白名單而非黑名單。
                if (!ctype.Contains("range"))
                    return null;

                uint id = Convert.ToUInt32(Prop(ctrl, "Id") ?? (object)0u);
                string name = Prop(ctrl, "Name") == null ? ("控制項#" + id) : Prop(ctrl, "Name").ToString();
                string category = Prop(ctrl, "Category") == null ? "" : Prop(ctrl, "Category").ToString();
                string unit = Prop(ctrl, "Units") == null ? "" : Prop(ctrl, "Units").ToString();
                double def = Dec(Prop(ctrl, "DefaultValue"));
                double active = Dec(Prop(ctrl, "ActiveValue"));
                double boot = Dec(Prop(ctrl, "BootValue"));
                bool ro = Prop(ctrl, "ReadOnly") as bool? ?? false;
                bool en = Prop(ctrl, "Enabled") as bool? ?? true;
                bool rr = Prop(ctrl, "RequiresReboot") as bool? ?? false;

                double min = ParseD(Call(ctrl, "GetMinPossibleValue")) ?? active;
                double max = ParseD(Call(ctrl, "GetMaxPossibleValue")) ?? active;
                if (max < min) { double tmp = min; min = max; max = tmp; }
                if (max <= min) return null;   // 無可調區間（可能被鎖），略過以保持清單皆可操作

                // 部分「自適應／傳統」旋鈕（如全域 Core Voltage #2、Core Voltage Offset #34）其上限
                // 回傳 uint.MaxValue（4294967295）哨兵值，代表 SDK 未給定真實上界 —— 無法安全呈現為滑桿。
                // 真實旋鈕上限遠低於此（功率≈4096W、電流≈1024A、比率≤255、電壓≤2V），故以寬鬆理性上界過濾；
                // 同一物理量的逐核心旋鈕（#140–157 等）皆有正確界限，功能不致遺失。
                const double SaneBound = 1e6;
                if (double.IsNaN(min) || double.IsNaN(max)) return null;
                if (max >= SaneBound || min <= -SaneBound) return null;

                bool rt = false;
                try { rt = Call(_tuning, "IsControlTunableRealTime", id) as bool? ?? false; } catch { }

                return new KnobData
                {
                    Id = id,
                    Name = name,
                    Category = category,
                    Unit = unit,
                    Min = min,
                    Max = max,
                    Default = def,
                    Boot = boot,
                    Active = active,
                    RealTime = rt,
                    RequiresReboot = rr,
                    ReadOnly = ro,
                    Enabled = en,
                };
            }
            catch { return null; }
        }

        // ── 診斷：列出 TuningLib 相關方法與「未經篩選」的原始控制項，找出 KNOBS=0 之因 ──
        public string DumpDiagnostics()
        {
            var sb = new System.Text.StringBuilder();
            if (_tuning == null || _tuningType == null) return "（TuningLib 為 null，無法診斷）";

            sb.AppendLine("── TuningLib 型別：" + _tuningType.FullName);
            sb.AppendLine("── 相關方法：");
            foreach (var m in _tuningType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                string n = m.Name;
                if (n.IndexOf("Control", StringComparison.OrdinalIgnoreCase) < 0
                    && n.IndexOf("Tune", StringComparison.OrdinalIgnoreCase) < 0
                    && n.IndexOf("Available", StringComparison.OrdinalIgnoreCase) < 0
                    && n.IndexOf("Tunable", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                sb.AppendLine("    " + m.ReturnType.Name + " " + n + "(" + ps + ")");
            }

            object list = null;
            try { list = _mGetAvailable != null ? _mGetAvailable.Invoke(_tuning, null) : null; }
            catch (Exception ex) { sb.AppendLine("── GetAvailableControls 擲出：" + ex.Message); }
            var seq = list as IEnumerable;
            if (seq == null) { sb.AppendLine("── GetAvailableControls 回傳 null 或非集合（type=" + (list == null ? "null" : list.GetType().Name) + "）"); return sb.ToString(); }

            int raw = 0;
            foreach (var ctrl in seq)
            {
                raw++;
                if (ctrl == null) { sb.AppendLine("  [null 控制項]"); continue; }
                Type ct = ctrl.GetType();
                sb.AppendLine("  ● 控制項型別=" + ct.Name);
                foreach (var p in ct.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    object v; try { v = p.GetValue(ctrl); } catch (Exception ex) { v = "<擲出:" + ex.GetType().Name + ">"; }
                    sb.AppendLine("      " + p.Name + " = " + (v == null ? "null" : v.ToString()));
                }
                object mn = null, mx = null;
                try { mn = Call(ctrl, "GetMinPossibleValue"); } catch (Exception ex) { mn = "<擲出:" + ex.GetType().Name + ">"; }
                try { mx = Call(ctrl, "GetMaxPossibleValue"); } catch (Exception ex) { mx = "<擲出:" + ex.GetType().Name + ">"; }
                sb.AppendLine("      GetMinPossibleValue() = " + (mn == null ? "null" : mn.ToString()));
                sb.AppendLine("      GetMaxPossibleValue() = " + (mx == null ? "null" : mx.ToString()));
                var built = BuildKnob(ctrl);
                sb.AppendLine("      → BuildKnob = " + (built == null ? "被篩掉(null)" : ("保留 range " + built.Min + ".." + built.Max)));
            }
            sb.AppendLine("── 原始控制項數=" + raw + "，篩選後=" + _knobs.Count);
            return sb.ToString();
        }

        // ── 深度探勘：完整傾印 SDK 表面，找出此版本真正的「列舉控制項」API ──────────
        public string ExploreSdk()
        {
            var sb = new System.Text.StringBuilder();
            if (_facade == null) return "（facade 為 null）";

            DumpType(sb, _facade.GetType(), "IntelOverclockingLibrary (facade)");
            if (_tuningType != null) DumpType(sb, _tuningType, "TuningLib");

            object sysinfo = null;
            try { sysinfo = Prop(_facade, "SystemInfoLib"); } catch { }
            if (sysinfo != null) DumpType(sb, sysinfo.GetType(), "SystemInfoLib");

            object mon = null;
            try { mon = Prop(_facade, "MonitoringLib"); } catch { }
            if (mon != null) DumpType(sb, mon.GetType(), "MonitoringLib");

            // 列出所有 enum（尤其控制項 ID 集合可能就藏在這裡）
            sb.AppendLine();
            sb.AppendLine("═══ 相關 enum（Control / Tuning / Device / Id）：");
            foreach (var t in SafeTypes())
            {
                if (!t.IsEnum) continue;
                string n = t.Name;
                if (n.IndexOf("Control", StringComparison.OrdinalIgnoreCase) < 0
                    && n.IndexOf("Tuning", StringComparison.OrdinalIgnoreCase) < 0
                    && n.IndexOf("Device", StringComparison.OrdinalIgnoreCase) < 0
                    && n.IndexOf("Id", StringComparison.OrdinalIgnoreCase) < 0) continue;
                sb.AppendLine("  ● enum " + t.FullName);
                try
                {
                    var names = Enum.GetNames(t);
                    var vals = Enum.GetValues(t);
                    for (int i = 0; i < names.Length; i++)
                        sb.AppendLine("      " + Convert.ToInt64(vals.GetValue(i)) + " = " + names[i]);
                }
                catch (Exception ex) { sb.AppendLine("      <列舉失敗:" + ex.Message + ">"); }
            }

            // 所有型別名稱（了解模型全貌）
            sb.AppendLine();
            sb.AppendLine("═══ 組件內所有公開型別：");
            foreach (var t in SafeTypes().Where(t => t.IsPublic).OrderBy(t => t.FullName))
                sb.AppendLine("  " + t.FullName + (t.IsEnum ? " [enum]" : t.IsInterface ? " [interface]" : ""));

            return sb.ToString();
        }

        private Type[] SafeTypes()
        {
            try { return _sdkAsm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
            catch { return new Type[0]; }
        }

        private static void DumpType(System.Text.StringBuilder sb, Type t, string title)
        {
            sb.AppendLine();
            sb.AppendLine("═══ " + title + " : " + t.FullName);
            sb.AppendLine("  — 屬性：");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                string idx = p.GetIndexParameters().Length > 0
                    ? "[" + string.Join(",", p.GetIndexParameters().Select(x => x.ParameterType.Name)) + "]" : "";
                sb.AppendLine("    " + p.PropertyType.Name + " " + p.Name + idx);
            }
            sb.AppendLine("  — 方法：");
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.IsSpecialName) continue; // 略過 property/event 存取子
                string ps = string.Join(", ", m.GetParameters().Select(p =>
                    (p.ParameterType.IsByRef ? "ref " : "") + p.ParameterType.Name + " " + p.Name));
                sb.AppendLine("    " + m.ReturnType.Name + " " + m.Name + "(" + ps + ")");
            }
        }

        // ── 探勘元資料型別的實際形狀（KnobMetadata 目錄等），供撰寫真正的列舉邏輯 ──────
        public string ExploreMeta()
        {
            var sb = new System.Text.StringBuilder();
            string[] names =
            {
                "Intel.Overclocking.SDK.XtuMetadata.KnobMetadata",
                "Intel.Overclocking.SDK.XtuMetadata.KnobMetadataManager",
                "Intel.Overclocking.SDK.XtuMetadata.MonitorMetadata",
                "Intel.Overclocking.SDK.XtuMetadata.MonitorMetadataManager",
                "Intel.Overclocking.SDK.Tuning.ClientTuningControl",
                "Intel.Overclocking.SDK.Tuning.ClientTuningProposal",
                "Intel.Overclocking.SDK.Profile.XtuTuningChangeList",
                "Intel.Overclocking.SDK.Profile.TuningControlItem",
                "Intel.Overclocking.SDK.Profile.TuningItem",
                "Intel.Overclocking.SDK.Profile.TuningValue",
                "Intel.Overclocking.SDK.ServiceInfo.IProcessorInfo",
            };
            foreach (var n in names) DumpFull(sb, _sdkAsm.GetType(n));

            // 嘗試即時取得旋鈕目錄：找 KnobMetadataManager 的取得方式與旋鈕集合
            sb.AppendLine();
            sb.AppendLine("═══ 嘗試即時列舉 KnobMetadata：");
            try { TryEnumerateMeta(sb); }
            catch (Exception ex) { sb.AppendLine("  <例外:" + ex + ">"); }
            return sb.ToString();
        }

        private void TryEnumerateMeta(System.Text.StringBuilder sb)
        {
            Type mgrType = _sdkAsm.GetType("Intel.Overclocking.SDK.XtuMetadata.KnobMetadataManager");
            if (mgrType == null) { sb.AppendLine("  找不到 KnobMetadataManager"); return; }

            object mgr = null;
            // 單例：找公開 static 屬性 / 欄位 / 無參 static 方法
            foreach (var p in mgrType.GetProperties(BindingFlags.Public | BindingFlags.Static))
                if (mgrType.IsAssignableFrom(p.PropertyType)) { try { mgr = p.GetValue(null); sb.AppendLine("  由 static 屬性 " + p.Name + " 取得實例"); break; } catch { } }
            if (mgr == null)
                foreach (var f in mgrType.GetFields(BindingFlags.Public | BindingFlags.Static))
                    if (mgrType.IsAssignableFrom(f.FieldType)) { try { mgr = f.GetValue(null); sb.AppendLine("  由 static 欄位 " + f.Name + " 取得實例"); break; } catch { } }
            if (mgr == null)
                try { mgr = Activator.CreateInstance(mgrType); sb.AppendLine("  以無參建構子建立實例"); }
                catch (Exception ex) { sb.AppendLine("  無法建立 KnobMetadataManager：" + ex.Message); }
            if (mgr == null) return;

            // 找回傳集合的方法 / 屬性
            foreach (var m in mgrType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.IsSpecialName || m.GetParameters().Length != 0) continue;
                if (!typeof(IEnumerable).IsAssignableFrom(m.ReturnType)) continue;
                if (m.ReturnType == typeof(string)) continue;
                sb.AppendLine("  ▶ 方法 " + m.Name + "() 回傳 " + m.ReturnType.Name);
                try
                {
                    var seq = m.Invoke(mgr, null) as IEnumerable;
                    DumpMetaSeq(sb, seq);
                }
                catch (Exception ex) { sb.AppendLine("    <呼叫失敗:" + ex.Message + ">"); }
            }
            foreach (var p in mgrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (!typeof(IEnumerable).IsAssignableFrom(p.PropertyType) || p.PropertyType == typeof(string)) continue;
                sb.AppendLine("  ▶ 屬性 " + p.Name + " 型別 " + p.PropertyType.Name);
                try { DumpMetaSeq(sb, p.GetValue(mgr) as IEnumerable); }
                catch (Exception ex) { sb.AppendLine("    <讀取失敗:" + ex.Message + ">"); }
            }
        }

        private void DumpMetaSeq(System.Text.StringBuilder sb, IEnumerable seq)
        {
            if (seq == null) { sb.AppendLine("    （null）"); return; }
            int i = 0;
            foreach (var item in seq)
            {
                if (item == null) continue;
                if (i++ >= 6) { sb.AppendLine("    …（其餘略，共列舉中）"); break; }
                object it = item;
                // 若為 KeyValuePair，取 Value 為真正的 metadata
                var vp = it.GetType().GetProperty("Value");
                if (it.GetType().Name.StartsWith("KeyValuePair") && vp != null) it = vp.GetValue(item);
                sb.AppendLine("    ── " + it.GetType().Name + " ──");
                foreach (var p in it.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    object v; try { v = p.GetValue(it); } catch { v = "<擲出>"; }
                    sb.AppendLine("       " + p.Name + " = " + (v == null ? "null" : v.ToString()));
                }
            }
            if (i == 0) sb.AppendLine("    （空集合）");
        }

        private static void DumpFull(System.Text.StringBuilder sb, Type t)
        {
            sb.AppendLine();
            if (t == null) { sb.AppendLine("═══ （找不到型別）"); return; }
            sb.AppendLine("═══ " + t.FullName + (t.IsInterface ? " [interface]" : t.IsEnum ? " [enum]" : ""));
            foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                sb.AppendLine("  ctor " + (c.IsPublic ? "pub" : "int") + "(" + Sig(c.GetParameters()) + ")");
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                sb.AppendLine("  F " + f.FieldType.Name + " " + f.Name);
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                sb.AppendLine("  P " + p.PropertyType.Name + " " + p.Name
                    + (p.GetIndexParameters().Length > 0 ? "[" + Sig(p.GetIndexParameters()) + "]" : ""));
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.IsSpecialName) continue;
                sb.AppendLine("  M " + m.ReturnType.Name + " " + m.Name + "(" + Sig(m.GetParameters()) + ")");
            }
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                sb.AppendLine("  Fs " + f.FieldType.Name + " " + f.Name);
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Static))
                sb.AppendLine("  Ps " + p.PropertyType.Name + " " + p.Name);
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.IsSpecialName) continue;
                sb.AppendLine("  Ms " + m.ReturnType.Name + " " + m.Name + "(" + Sig(m.GetParameters()) + ")");
            }
        }

        private static string Sig(ParameterInfo[] ps)
        {
            return string.Join(", ", ps.Select(p => (p.ParameterType.IsByRef ? "ref " : "") + p.ParameterType.Name + " " + p.Name));
        }

        // ── 實驗：找出「能取得含 min/max/active 的完整控制項清單」的真正來源 ──────────
        public string Experiment()
        {
            var sb = new System.Text.StringBuilder();

            // (1) KnobMetadataManager 目錄（需先 Initialize）
            sb.AppendLine("═══ (1) KnobMetadataManager.GetAllEntries()（先 Initialize）：");
            List<uint> metaIds = new List<uint>();
            try
            {
                Type mgrType = _sdkAsm.GetType("Intel.Overclocking.SDK.XtuMetadata.KnobMetadataManager");
                object mgr = Activator.CreateInstance(mgrType);
                try { Call(mgr, "Initialize"); sb.AppendLine("  Initialize() 成功"); }
                catch (Exception ex) { sb.AppendLine("  Initialize() 擲出：" + Inner(ex)); }
                var entries = Call(mgr, "GetAllEntries") as IEnumerable;
                int n = 0;
                if (entries != null)
                    foreach (var e in entries)
                    {
                        n++;
                        uint cid = 0;
                        try { cid = Convert.ToUInt32(Prop(e, "ControlId") ?? (object)0u); } catch { }
                        string ctype = Prop(e, "ControlType")?.ToString() ?? "";
                        if (cid != 0) metaIds.Add(cid);
                        if (n <= 40)
                            sb.AppendLine("  #" + cid + " [" + Prop(e, "Name") + "] type=" + ctype
                                + " cat=" + Prop(e, "Category") + " unit=" + Prop(e, "Units") + " fmt=" + Prop(e, "FormatString"));
                    }
                sb.AppendLine("  目錄總數=" + n + (n > 40 ? "（僅列前 40）" : ""));
            }
            catch (Exception ex) { sb.AppendLine("  <例外:" + Inner(ex) + ">"); }

            // (2) DiscardChanges() 回傳的 List（初始無待套用變更，等同無害快照）
            sb.AppendLine();
            sb.AppendLine("═══ (2) DiscardChanges() 回傳內容：");
            try
            {
                object dc = _mDiscard != null ? _mDiscard.Invoke(_tuning, null) : null;
                DumpControlSeq(sb, dc as IEnumerable);
            }
            catch (Exception ex) { sb.AppendLine("  <例外:" + Inner(ex) + ">"); }

            // (3) GetActiveTuningProfile().ProposedValues
            sb.AppendLine();
            sb.AppendLine("═══ (3) GetActiveTuningProfile().ProposedValues：");
            try
            {
                object prof = Call(_tuning, "GetActiveTuningProfile");
                object pv = Prop(prof, "ProposedValues");
                DumpControlSeq(sb, pv as IEnumerable);
            }
            catch (Exception ex) { sb.AppendLine("  <例外:" + Inner(ex) + ">"); }

            // (4) 逐一以 metaId 檢查 IsControlTunable + 目前值，並嘗試建構 ClientTuningControl 讀 min/max
            sb.AppendLine();
            sb.AppendLine("═══ (4) 前 12 個目錄項：IsControlTunable / GetTuningControlByID / ClientTuningControl 探測：");
            Type cccType = _sdkAsm.GetType("Intel.Overclocking.SDK.Tuning.ClientTuningControl");
            int shown = 0;
            foreach (var cid in metaIds)
            {
                if (shown++ >= 12) break;
                bool tun = false; double cur = double.NaN;
                try { tun = Call(_tuning, "IsControlTunable", cid) as bool? ?? false; } catch { }
                try { cur = Dec(Call(_tuning, "GetTuningControlByID", cid)); } catch { }
                string line = "  #" + cid + " tunable=" + tun + " value=" + cur;
                if (cccType != null)
                {
                    try
                    {
                        object ccc = Activator.CreateInstance(cccType);
                        SetProp(ccc, "Id", cid);
                        object mn = Call(ccc, "GetMinPossibleValue");
                        object mx = Call(ccc, "GetMaxPossibleValue");
                        line += " | ccc.min=" + mn + " max=" + mx + " active=" + Prop(ccc, "ActiveValue") + " name=" + Prop(ccc, "Name");
                    }
                    catch (Exception ex) { line += " | ccc<例外:" + Inner(ex) + ">"; }
                }
                sb.AppendLine(line);
            }

            return sb.ToString();
        }

        private void DumpControlSeq(System.Text.StringBuilder sb, IEnumerable seq)
        {
            if (seq == null) { sb.AppendLine("  （null）"); return; }
            int i = 0;
            foreach (var c in seq)
            {
                if (c == null) continue;
                if (i == 0) sb.AppendLine("  元素型別=" + c.GetType().FullName);
                if (i++ >= 6) { sb.AppendLine("  …（其餘略）"); break; }
                string mn = "", mx = "";
                try { mn = Call(c, "GetMinPossibleValue")?.ToString() ?? ""; } catch { }
                try { mx = Call(c, "GetMaxPossibleValue")?.ToString() ?? ""; } catch { }
                sb.AppendLine("    #" + Prop(c, "Id") + " [" + Prop(c, "Name") + "] type=" + Prop(c, "ControlType")
                    + " active=" + Prop(c, "ActiveValue") + " def=" + Prop(c, "DefaultValue")
                    + " min=" + mn + " max=" + mx + " ro=" + Prop(c, "ReadOnly") + " en=" + Prop(c, "Enabled"));
            }
            if (i == 0) sb.AppendLine("  （空集合）");
        }

        private static string Inner(Exception ex)
        {
            var r = ex; while (r.InnerException != null) r = r.InnerException;
            return r.Message + "(" + r.GetType().Name + ")";
        }

        // ── 尋找「回傳含 min/max 之完整控制項」的 API：掃描整個組件 ────────────────
        public string FindControlSource()
        {
            var sb = new System.Text.StringBuilder();

            // ProfileLib / ProcessLib / AppProfileLib 表面
            foreach (var libProp in new[] { "ProfileLib", "ProcessLib", "AppProfileLib" })
            {
                object lib = null;
                try { lib = Prop(_facade, libProp); } catch { }
                if (lib != null) DumpFull(sb, lib.GetType());
            }

            // 全組件掃描：回傳型別涉及 ClientTuningControl / TuningControlItem 的成員
            sb.AppendLine();
            sb.AppendLine("═══ 全組件掃描：回傳涉及 ClientTuningControl / TuningControlItem 的方法/屬性：");
            string[] needles = { "ClientTuningControl", "TuningControlItem" };
            foreach (var t in SafeTypes())
            {
                if (!t.IsPublic && !t.IsNestedPublic) continue;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (m.IsSpecialName) continue;
                    if (needles.Any(n => TypeMentions(m.ReturnType, n)))
                        sb.AppendLine("  M " + t.Name + "." + m.Name + "(" + Sig(m.GetParameters()) + ") -> " + FriendlyType(m.ReturnType));
                }
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (needles.Any(n => TypeMentions(p.PropertyType, n)))
                        sb.AppendLine("  P " + t.Name + "." + p.Name + " -> " + FriendlyType(p.PropertyType));
                }
            }

            return sb.ToString();
        }

        private static bool TypeMentions(Type t, string needle)
        {
            if (t == null) return false;
            if (t.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IsGenericType)
                foreach (var a in t.GetGenericArguments())
                    if (TypeMentions(a, needle)) return true;
            if (t.IsArray) return TypeMentions(t.GetElementType(), needle);
            return false;
        }

        private static string FriendlyType(Type t)
        {
            if (t == null) return "?";
            if (t.IsGenericType)
                return t.Name + "<" + string.Join(",", t.GetGenericArguments().Select(FriendlyType)) + ">";
            return t.Name;
        }

        // ── 讀取 ───────────────────────────────────────────────────────────────
        public double? ReadCoreVoltage()
        {
            try
            {
                double v = Dec(Call(_tuning, "GetCoreVoltageValue"));
                return v > 0 ? (double?)v : null;
            }
            catch { return null; }
        }

        private object _monitoring;
        private bool _monStarted;
        private Dictionary<uint, string> _monNames;

        public double? ReadMonitor(string[] nameContains)
        {
            try
            {
                if (_monitoring == null) _monitoring = Prop(_facade, "MonitoringLib");
                if (_monitoring == null) return null;
                if (!_monStarted) { try { Call(_monitoring, "Start"); } catch { } _monStarted = true; }
                BuildMonitorNames();
                if (_monNames == null || _monNames.Count == 0) return null;
                var dict = Call(_monitoring, "GetValues") as IEnumerable;
                if (dict == null) return null;

                foreach (var want in nameContains)
                {
                    foreach (var kv in _monNames)
                    {
                        if (kv.Value.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        double? v = ReadDictValue(dict, kv.Key);
                        if (v.HasValue && v.Value != 0) return v;
                    }
                }
            }
            catch { }
            return null;
        }

        private void BuildMonitorNames()
        {
            if (_monNames != null) return;
            var map = new Dictionary<uint, string>();
            try
            {
                var mons = Call(_monitoring, "GetAvailableMonitors") as IEnumerable;
                if (mons != null)
                    foreach (var m in mons)
                    {
                        try
                        {
                            uint id = Convert.ToUInt32(Prop(m, "Id") ?? (object)0u);
                            string name = Prop(m, "Name") == null ? "" : Prop(m, "Name").ToString();
                            if (name.Length > 0) map[id] = name;
                        }
                        catch { }
                    }
            }
            catch { }
            _monNames = map;
        }

        private static double? ReadDictValue(IEnumerable dict, uint id)
        {
            foreach (var entry in dict)
            {
                try
                {
                    object key = Prop(entry, "Key");
                    if (key == null) continue;
                    if (Convert.ToUInt32(key) == id) return Dec(Prop(entry, "Value"));
                }
                catch { }
            }
            return null;
        }

        public Dictionary<uint, double> RefreshActives()
        {
            try { if (_mRefresh != null) _mRefresh.Invoke(_tuning, new object[] { true }); } catch { }
            var map = new Dictionary<uint, double>();
            foreach (var k in _knobs)
            {
                double? a = ReadActive(k.Id);
                if (a.HasValue) { k.Active = a.Value; map[k.Id] = a.Value; }
                else map[k.Id] = k.Active;
            }
            return map;
        }

        private double? ReadActive(uint id)
        {
            try
            {
                object ctrl = _mGetControl == null ? null : _mGetControl.Invoke(_tuning, new object[] { id });
                if (ctrl == null) return null;
                return Dec(Prop(ctrl, "ActiveValue"));
            }
            catch { return null; }
        }

        // ── 寫入 ───────────────────────────────────────────────────────────────
        public ApplyOut Apply(uint id, double value, bool requiresReboot)
        {
            var outp = new ApplyOut();
            if (Status != "Ready" || _mTune == null || _mApply == null)
            {
                outp.Ok = false;
                outp.Code = "NOT_READY";
                return outp;
            }
            try
            {
                lock (_lock)
                {
                    _mTune.Invoke(_tuning, new object[] { id, (decimal)value, requiresReboot });
                    object applyRes = _mApply.Invoke(_tuning, new object[] { false });
                    outp.Code = ResultCode(applyRes);
                    try { if (_mRefresh != null) _mRefresh.Invoke(_tuning, new object[] { true }); } catch { }
                }
                outp.Ok = true;
            }
            catch (Exception ex)
            {
                outp.Ok = false;
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                outp.Code = "EX:" + root.Message;
                return outp;
            }
            double? after = ReadActive(id);
            if (after.HasValue) { outp.ActiveKnown = true; outp.Active = after.Value; }
            return outp;
        }

        public bool Discard()
        {
            try
            {
                lock (_lock) { if (_mDiscard != null) _mDiscard.Invoke(_tuning, null); }
                RefreshActives();
                return true;
            }
            catch { return false; }
        }

        /// <summary>把所有可寫旋鈕還原為預設值；回傳已還原數與最新現值。</summary>
        public int RestoreDefaults(out Dictionary<uint, double> actives, out string error)
        {
            actives = new Dictionary<uint, double>();
            error = null;
            if (Status != "Ready" || _mTune == null || _mApply == null) { error = "NOT_READY"; return 0; }
            int n = 0;
            try
            {
                lock (_lock)
                {
                    foreach (var k in _knobs.Where(k => k.Writable))
                    {
                        _mTune.Invoke(_tuning, new object[] { k.Id, (decimal)k.Default, k.RequiresReboot });
                        n++;
                    }
                    _mApply.Invoke(_tuning, new object[] { false });
                    try { if (_mRefresh != null) _mRefresh.Invoke(_tuning, new object[] { true }); } catch { }
                }
            }
            catch (Exception ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                error = root.Message;
                return n;
            }
            actives = RefreshActives();
            return n;
        }

        public void SetBootRestore(bool on)
        {
            try { SetProp(_tuning, "DoRestoreUserValuesAtBoot", on); } catch { }
            try
            {
                MethodInfo m = _tuningType == null ? null :
                    _tuningType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(x => x.Name == "SetSuspendRestoreOptions" && x.GetParameters().Length >= 1);
                if (m != null)
                {
                    var ps = m.GetParameters();
                    var args = new object[ps.Length];
                    args[0] = on;                        // restoreBootValueAtSuspend
                    for (int i = 1; i < ps.Length; i++)  // 其餘 List 參數給 null（排除清單留空）
                        args[i] = null;
                    m.Invoke(_tuning, args);
                }
            }
            catch { }
        }

        // ── Intel Speed Optimizer（可逆一鍵自動超頻）──────────────────────────
        public int SpeedOptimizerState()
        {
            try { return Convert.ToInt32(Call(_tuning, "GetSpeedOptimizerState") ?? 0); }
            catch { return 0; }
        }

        public bool SetSpeedOptimizer(bool on, bool extreme, out string error)
        {
            error = null;
            try
            {
                lock (_lock)
                {
                    bool done = false;
                    if (extreme)
                    {
                        Type isoType = _sdkAsm == null ? null : _sdkAsm.GetTypes().FirstOrDefault(t => t.Name == "IsoType");
                        MethodInfo m2 = isoType == null ? null : FindMethod(_tuningType, "UpdateSpeedOptimizerState", typeof(bool), isoType);
                        if (m2 != null)
                        {
                            object iso = Enum.Parse(isoType, "Extreme");
                            m2.Invoke(_tuning, new object[] { on, iso });
                            done = true;
                        }
                    }
                    if (!done)
                    {
                        MethodInfo m1 = FindMethod(_tuningType, "UpdateSpeedOptimizerState", typeof(bool));
                        if (m1 == null) { error = "SDK 缺少 UpdateSpeedOptimizerState 方法。"; return false; }
                        m1.Invoke(_tuning, new object[] { on });
                    }
                    if (_mApply != null) _mApply.Invoke(_tuning, new object[] { false });
                    try { if (_mRefresh != null) _mRefresh.Invoke(_tuning, new object[] { true }); } catch { }
                }
            }
            catch (Exception ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                error = root.Message;
                return false;
            }
            RefreshActives();
            return true;
        }

        // ── SDK 路徑定位 ───────────────────────────────────────────────────────
        private string LocateSdk()
        {
            foreach (var dir in ClientDirCandidates())
            {
                string dll = Path.Combine(dir, "IntelOverclockingSDK.dll");
                if (File.Exists(dll))
                {
                    _probeDirs.Clear();
                    _probeDirs.Add(dir);                                   // Client
                    string parent = Directory.GetParent(dir) == null ? null : Directory.GetParent(dir).FullName;
                    if (parent != null)
                    {
                        string drivers = Path.Combine(parent, "Drivers");
                        if (Directory.Exists(drivers)) _probeDirs.Add(drivers);
                        _probeDirs.Add(parent);
                    }
                    return dll;
                }
            }
            return null;
        }

        private static IEnumerable<string> ClientDirCandidates()
        {
            // 換機可攜：使用者於此檔填入 Client 目錄路徑即可套用到任何裝有 XTU 的電腦
            string cfg = Path.Combine(RootDir, "xtu_path.txt");
            if (File.Exists(cfg))
            {
                string p = "";
                try { p = File.ReadAllText(cfg).Trim(); } catch { }
                if (p.Length > 0)
                {
                    yield return p;
                    yield return Path.Combine(p, "Client");
                }
            }

            foreach (var pf in new[]
            {
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            })
            {
                if (string.IsNullOrEmpty(pf)) continue;
                string root = Path.Combine(pf, "Intel", "Intel(R) Extreme Tuning Utility");
                yield return Path.Combine(root, "Client");
                yield return root;
            }
        }

        // ── 反射小工具 ─────────────────────────────────────────────────────────
        private bool TryBool(string method)
        {
            try { return Call(_tuning, method) as bool? ?? false; } catch { return false; }
        }

        private static object Call(object target, string name, params object[] args)
        {
            if (target == null) return null;
            var m = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length);
            return m == null ? null : m.Invoke(target, args);
        }

        private static object Prop(object target, string name)
        {
            if (target == null) return null;
            var p = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return p == null ? null : p.GetValue(target);
        }

        private static void SetProp(object target, string name, object value)
        {
            var p = target == null ? null : target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite) p.SetValue(target, value);
        }

        private static MethodInfo FindMethod(Type t, string name, params Type[] paramTypes)
        {
            return t.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m =>
                   m.Name == name && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(paramTypes));
        }

        private static string ResultCode(object tuningResult)
        {
            try { var c = Prop(tuningResult, "GeneralCode"); return c == null ? null : c.ToString(); }
            catch { return null; }
        }

        private static object FindWatchdog(object root)
        {
            if (root == null) return null;
            if (root.GetType().GetMethod("IsWatchdogTimerPresent") != null) return root;
            Type t = root.GetType();
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object v; try { v = p.GetValue(root); } catch { continue; }
                if (v != null && v.GetType().GetMethod("IsWatchdogTimerPresent") != null) return v;
            }
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.GetParameters().Length != 0 || m.ReturnType == typeof(void)) continue;
                if (!m.Name.Contains("Watchdog") && !m.Name.Contains("Service") && !m.Name.StartsWith("Get")) continue;
                object v; try { v = m.Invoke(root, null); } catch { continue; }
                if (v != null && v.GetType().GetMethod("IsWatchdogTimerPresent") != null) return v;
            }
            return null;
        }

        private static double Dec(object o)
        {
            if (o == null) return 0;
            if (o is decimal) return (double)(decimal)o;
            if (o is double) return (double)o;
            if (o is float) return (float)o;
            if (o is int) return (int)o;
            if (o is uint) return (uint)o;
            if (o is long) return (long)o;
            if (o is string)
            {
                string s = (string)o;
                double r;
                if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out r)) return r;
                if (double.TryParse(s, out r)) return r;
                return 0;
            }
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        private static double? ParseD(object o)
        {
            if (o == null) return null;
            if (o is string)
            {
                string s = (string)o;
                double r;
                if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out r)) return r;
                if (double.TryParse(s, out r)) return r;
                return null;
            }
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return null; }
        }

        public void Dispose()
        {
            try { if (_monitoring != null && _monStarted) Call(_monitoring, "Stop"); } catch { }
            try { if (_facade != null) Call(_facade, "Reset"); } catch { }
            if (_resolver != null)
            {
                try { AppDomain.CurrentDomain.AssemblyResolve -= _resolver; } catch { }
                _resolver = null;
            }
        }
    }
}
