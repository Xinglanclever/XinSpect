using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace XtuBridge
{
    // XtuBridge 進入點。
    //   XtuBridge.exe           → JSON stdin/stdout 服務迴圈（XinSpect 本體以此驅動；預設模式）
    //   XtuBridge.exe --probe   → 人類可讀的自我檢測（用於「證明 Intel SDK 能在 net48 初始化」）
    //   --dump / --explore / --meta / --exp / --find → 保留於現場除錯用的反射傾印
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--probe")
                return Probe();

            if (args.Length > 0 && args[0] == "--dump")
                return Dump();

            if (args.Length > 0 && args[0] == "--explore")
                return Explore();

            if (args.Length > 0 && args[0] == "--meta")
                return Meta();

            if (args.Length > 0 && args[0] == "--exp")
                return Exp();

            if (args.Length > 0 && args[0] == "--find")
                return Find();

            return Server();   // 無參數：進入 JSON 服務迴圈
        }

        // ── JSON 服務迴圈 ────────────────────────────────────────────────────────
        // 協定：newline-delimited JSON，一行請求（{"cmd":"…", …}）對一行回應（{"ok":bool, …}）。
        // 本體（.NET 10）以重導管線啟動本程序，透過 stdin 送指令、stdout 收結果。
        private static int Server()
        {
            // 明確以 UTF-8（無 BOM）包住原始 std 控制代碼：確保中文旋鈕名稱雙向不亂碼，
            // 且不受主控台是否存在影響（本程序恆由 XinSpect 以重導管線啟動）。
            var enc = new UTF8Encoding(false);
            TextReader stdin = new StreamReader(Console.OpenStandardInput(), enc);
            var stdout = new StreamWriter(Console.OpenStandardOutput(), enc) { AutoFlush = true, NewLine = "\n" };

            // 防止 SDK 或相依組件的零星 Console 輸出污染協定管線（協定只走上面的 stdout 寫入器）。
            try { Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null); } catch { }

            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            XtuCore core = new XtuCore();
            try
            {
                string line;
                while ((line = stdin.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;

                    Dictionary<string, object> req = null;
                    try { req = ser.DeserializeObject(line) as Dictionary<string, object>; }
                    catch (Exception ex) { WriteJson(stdout, ser, Err("JSON 解析失敗：" + ex.Message)); continue; }
                    if (req == null) { WriteJson(stdout, ser, Err("請求非 JSON 物件")); continue; }

                    string cmd = Str(req, "cmd") ?? "";
                    if (cmd == "quit") { WriteJson(stdout, ser, Ok()); break; }

                    object resp;
                    try { resp = Dispatch(core, cmd, req); }
                    catch (Exception ex)
                    {
                        Exception r = ex; while (r.InnerException != null) r = r.InnerException;
                        resp = Err(r.Message + "(" + r.GetType().Name + ")");
                    }
                    WriteJson(stdout, ser, resp);
                }
            }
            finally { core.Dispose(); }
            return 0;
        }

        // ── 指令分派：把 JSON 請求轉為 XtuCore 呼叫，再包成 JSON 回應 ────────────
        private static object Dispatch(XtuCore core, string cmd, Dictionary<string, object> req)
        {
            switch (cmd)
            {
                case "ping":
                    return Ok();

                case "init":
                {
                    InitResult r = core.Initialize();
                    var knobs = new List<object>();
                    foreach (var k in r.Knobs)
                        knobs.Add(new Dictionary<string, object>
                        {
                            { "id", k.Id }, { "name", k.Name }, { "category", k.Category }, { "unit", k.Unit },
                            { "min", k.Min }, { "max", k.Max }, { "default", k.Default }, { "boot", k.Boot }, { "active", k.Active },
                            { "realTime", k.RealTime }, { "requiresReboot", k.RequiresReboot },
                            { "readOnly", k.ReadOnly }, { "enabled", k.Enabled },
                        });
                    return new Dictionary<string, object>
                    {
                        { "ok", true },
                        { "status", r.Status }, { "message", r.Message }, { "family", r.Family },
                        { "coreTunable", r.CoreTunable }, { "bclkTunable", r.BclkTunable },
                        { "memoryTunable", r.MemoryTunable }, { "cacheTunable", r.CacheTunable },
                        { "speedOptimizerSupported", r.SpeedOptimizerSupported },
                        { "watchdogPresent", r.WatchdogPresent }, { "watchdogRunning", r.WatchdogRunning },
                        { "watchdogFailed", r.WatchdogFailed },
                        { "knobs", knobs },
                    };
                }

                case "apply":
                {
                    ApplyOut a = core.Apply(U32(req, "id"), Dbl(req, "value"), Bool(req, "requiresReboot"));
                    return new Dictionary<string, object>
                    {
                        { "ok", a.Ok }, { "code", a.Code }, { "activeKnown", a.ActiveKnown }, { "active", a.Active },
                    };
                }

                case "discard":
                    return new Dictionary<string, object> { { "ok", core.Discard() } };

                case "restoreDefaults":
                {
                    Dictionary<uint, double> actives; string error;
                    int n = core.RestoreDefaults(out actives, out error);
                    return new Dictionary<string, object>
                    {
                        { "ok", error == null }, { "count", n }, { "actives", ToStrMap(actives) }, { "error", error },
                    };
                }

                case "refresh":
                    return new Dictionary<string, object>
                    {
                        { "ok", true }, { "actives", ToStrMap(core.RefreshActives()) },
                    };

                case "readVcore":
                {
                    double? v = core.ReadCoreVoltage();
                    return new Dictionary<string, object> { { "ok", true }, { "value", v.HasValue ? (object)v.Value : null } };
                }

                case "readMonitor":
                {
                    double? v = core.ReadMonitor(StrArray(req, "keys"));
                    return new Dictionary<string, object> { { "ok", true }, { "value", v.HasValue ? (object)v.Value : null } };
                }

                case "setBootRestore":
                    core.SetBootRestore(Bool(req, "on"));
                    return Ok();

                case "soState":
                    return new Dictionary<string, object> { { "ok", true }, { "value", core.SpeedOptimizerState() } };

                case "setSO":
                {
                    string error;
                    bool ok = core.SetSpeedOptimizer(Bool(req, "on"), Bool(req, "extreme"), out error);
                    return new Dictionary<string, object> { { "ok", ok }, { "error", error } };
                }

                default:
                    return Err("未知指令：" + cmd);
            }
        }

        // ── JSON 輔助 ────────────────────────────────────────────────────────────
        private static void WriteJson(StreamWriter w, JavaScriptSerializer ser, object obj)
        {
            w.WriteLine(ser.Serialize(obj));   // Serialize 不含換行；WriteLine 補上 \n 分隔
        }

        private static Dictionary<string, object> Ok()
        {
            return new Dictionary<string, object> { { "ok", true } };
        }

        private static Dictionary<string, object> Err(string msg)
        {
            return new Dictionary<string, object> { { "ok", false }, { "error", msg } };
        }

        // uint→double 對照表以字串鍵輸出（JSON 物件鍵必為字串），本體端再還原為 uint。
        private static Dictionary<string, object> ToStrMap(Dictionary<uint, double> m)
        {
            var d = new Dictionary<string, object>();
            if (m != null)
                foreach (var kv in m) d[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
            return d;
        }

        private static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? v.ToString() : null;
        }

        private static double Dbl(Dictionary<string, object> d, string k)
        {
            object v;
            if (d.TryGetValue(k, out v) && v != null)
                try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { }
            return 0;
        }

        // JavaScriptSerializer 依大小把整數解析為 Int32/Int64/Decimal，一律先轉 Int64 再收斂為 uint。
        private static uint U32(Dictionary<string, object> d, string k)
        {
            object v;
            if (d.TryGetValue(k, out v) && v != null)
                try { return Convert.ToUInt32(Convert.ToInt64(v, CultureInfo.InvariantCulture)); } catch { }
            return 0;
        }

        private static bool Bool(Dictionary<string, object> d, string k)
        {
            object v;
            if (d.TryGetValue(k, out v) && v != null)
            {
                if (v is bool) return (bool)v;
                try { return Convert.ToBoolean(v); } catch { }
            }
            return false;
        }

        private static string[] StrArray(Dictionary<string, object> d, string k)
        {
            object v;
            if (d.TryGetValue(k, out v))
            {
                var arr = v as object[];
                if (arr != null)
                {
                    var list = new List<string>();
                    foreach (var o in arr) if (o != null) list.Add(o.ToString());
                    return list.ToArray();
                }
            }
            return new string[0];
        }

        // ── 診斷：傾印未經篩選的原始控制項，找出 KNOBS=0 的成因 ──────────────────
        private static int Dump()
        {
            var core = new XtuCore();
            try
            {
                InitResult r = core.Initialize();
                Console.WriteLine("STATUS=" + r.Status + " FAMILY=" + r.Family + " KNOBS=" + r.Knobs.Count);
                Console.WriteLine(core.DumpDiagnostics());
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("DUMP_EX=" + ex);
                return 3;
            }
            finally { core.Dispose(); }
        }

        // ── 深度探勘 SDK 表面 ────────────────────────────────────────────────────
        private static int Explore()
        {
            var core = new XtuCore();
            try
            {
                core.Initialize();
                Console.WriteLine(core.ExploreSdk());
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXPLORE_EX=" + ex);
                return 3;
            }
            finally { core.Dispose(); }
        }

        // ── 探勘元資料型別形狀 ───────────────────────────────────────────────────
        private static int Meta()
        {
            var core = new XtuCore();
            try
            {
                core.Initialize();
                Console.WriteLine(core.ExploreMeta());
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("META_EX=" + ex);
                return 3;
            }
            finally { core.Dispose(); }
        }

        // ── 實驗：找出完整控制項清單的真正來源 ───────────────────────────────────
        private static int Exp()
        {
            var core = new XtuCore();
            try
            {
                core.Initialize();
                Console.WriteLine(core.Experiment());
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXP_EX=" + ex);
                return 3;
            }
            finally { core.Dispose(); }
        }

        // ── 掃描：回傳完整控制項的 API ────────────────────────────────────────────
        private static int Find()
        {
            var core = new XtuCore();
            try
            {
                core.Initialize();
                Console.WriteLine(core.FindControlSource());
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FIND_EX=" + ex);
                return 3;
            }
            finally { core.Dispose(); }
        }

        // ── 自我檢測：載入 SDK → 建構 → 取 TuningLib → 列舉控制項，全程以文字輸出 ──────
        private static int Probe()
        {
            var core = new XtuCore();
            try
            {
                InitResult r = core.Initialize();
                Console.WriteLine("STATUS=" + r.Status);
                Console.WriteLine("MESSAGE=" + r.Message);
                Console.WriteLine("FAMILY=" + r.Family);
                Console.WriteLine("CAPS core=" + r.CoreTunable + " bclk=" + r.BclkTunable
                                  + " mem=" + r.MemoryTunable + " cache=" + r.CacheTunable
                                  + " speedOpt=" + r.SpeedOptimizerSupported);
                Console.WriteLine("WATCHDOG present=" + r.WatchdogPresent
                                  + " running=" + r.WatchdogRunning + " failed=" + r.WatchdogFailed);
                Console.WriteLine("KNOBS=" + r.Knobs.Count);

                int i = 0;
                foreach (var k in r.Knobs)
                {
                    if (i++ >= 16) { Console.WriteLine("  …（其餘略）"); break; }
                    Console.WriteLine(string.Format(
                        "  #{0} [{1}] cat={2} unit={3} range={4}..{5} def={6} active={7} ro={8} en={9} rr={10} rt={11}",
                        k.Id, k.Name, k.Category, k.Unit, k.Min, k.Max, k.Default, k.Active,
                        k.ReadOnly, k.Enabled, k.RequiresReboot, k.RealTime));
                }

                double? vcore = core.ReadCoreVoltage();
                Console.WriteLine("VCORE=" + (vcore.HasValue ? vcore.Value.ToString("0.####") : "n/a"));
                return r.Status == "Ready" ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("PROBE_EX=" + ex);
                return 3;
            }
            finally
            {
                core.Dispose();
            }
        }
    }
}
