using System.Windows;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 命令面板的項目來源：把導覽註冊表、實用工具子註冊表與全域動作攤平成一份可搜尋清單。
/// 每次開啟面板時重建（動作標題含即時狀態，如「切換至淺色主題」）。
/// </summary>
public static class PaletteCatalog
{
    // 動作類項目共用的小圖示（避免每筆重複解析）
    private static readonly Geometry ActionIcon = Freeze("F1 M13,2 L4,14 L10,14 L9,22 L20,9 L13,9 Z");
    private static readonly Geometry GearIcon = Freeze("F1 M12,9 a3,3 0 1,0 0.01,0 Z M10.8,2 h2.4 l0.4,2.1 a8,8 0 0 1 1.7,1 l2,-0.8 1.6,2.7 -1.6,1.4 "
                                                     + "a8,8 0 0 1 0,1.9 l1.6,1.4 -1.6,2.7 -2,-0.8 a8,8 0 0 1 -1.7,1 l-0.4,2.1 h-2.4 l-0.4,-2.1 "
                                                     + "a8,8 0 0 1 -1.7,-1 l-2,0.8 -1.6,-2.7 1.6,-1.4 a8,8 0 0 1 0,-1.9 l-1.6,-1.4 1.6,-2.7 2,0.8 "
                                                     + "a8,8 0 0 1 1.7,-1 Z");

    private static Geometry Freeze(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    /// <summary>建立本次開啟的完整項目清單：頁面 → 子工具 → 全域動作。</summary>
    public static List<PaletteItem> Build(MainWindow shell, MainViewModel vm)
    {
        var list = new List<PaletteItem>(PageRegistry.Pages.Count + PageRegistry.Utilities.Count + 16);

        // ── 分頁 ──────────────────────────────────────────
        foreach (var d in PageRegistry.Pages)
        {
            var def = d;
            list.Add(new PaletteItem
            {
                Title = def.Title,
                Subtitle = def.Hint ?? "",
                Kind = "頁面",
                Icon = def.Icon,
                Keywords = def.Keywords,
                Invoke = () => shell.NavigateToKey(def.Key),
            });
        }

        // ── 實用工具子頁（深層跳轉：先進容器頁，再選子工具）──
        foreach (var d in PageRegistry.Utilities)
        {
            var def = d;
            list.Add(new PaletteItem
            {
                Title = def.Title,
                Subtitle = def.Hint ?? "",
                Kind = "工具",
                Icon = def.Icon,
                Keywords = def.Keywords,
                Invoke = () => shell.NavigateToUtility(def.Key),
            });
        }

        // ── 全域動作 ──────────────────────────────────────
        list.Add(new PaletteItem
        {
            Title = "匯出完整報告",
            Subtitle = "把目前所有硬體資訊與感測讀值輸出為報告檔",
            Kind = "動作", Icon = ActionIcon,
            Keywords = ["export", "report", "匯出", "報告", "html"],
            Invoke = vm.ExportReport,
        });

        list.Add(new PaletteItem
        {
            Title = "切換迷你浮動監視器",
            Subtitle = "在桌面最上層顯示精簡的即時數據條",
            Kind = "動作", Icon = ActionIcon,
            Keywords = ["mini", "overlay", "迷你", "浮動", "監視"],
            Invoke = shell.ToggleMini,
        });

        list.Add(new PaletteItem
        {
            Title = ThemeService.Theme == AppTheme.Dark ? "切換為淺色主題" : "切換為深色主題",
            Subtitle = "立即套用，並記憶為下次啟動的外觀",
            Kind = "外觀", Icon = GearIcon,
            Keywords = ["theme", "dark", "light", "主題", "深色", "淺色", "外觀"],
            Invoke = ThemeService.ToggleTheme,
        });

        foreach (var a in ThemeService.Presets)
        {
            var accent = a;
            list.Add(new PaletteItem
            {
                Title = $"強調色：{accent.Name}",
                Subtitle = accent.Main,
                Kind = "外觀", Icon = GearIcon,
                Keywords = ["accent", "color", "強調色", "配色", accent.Key],
                Invoke = () => ThemeService.Accent = accent,
            });
        }

        list.Add(new PaletteItem
        {
            Title = "所有功能一鍵初始化",
            Subtitle = "重新偵測硬體、感測器、超頻引擎與 winget",
            Kind = "動作", Icon = GearIcon,
            Keywords = ["reinit", "refresh", "初始化", "重整", "重新偵測"],
            Invoke = () => _ = vm.ReinitializeAllAsync(),
        });

        list.Add(new PaletteItem
        {
            Title = "執行環境自檢",
            Subtitle = "檢查各功能所需的執行階段、驅動與服務是否就緒",
            Kind = "動作", Icon = GearIcon,
            Keywords = ["env", "check", "自檢", "環境", "診斷"],
            Invoke = () => _ = vm.EnvCheck.RunAsync(vm),
        });

        list.Add(new PaletteItem
        {
            Title = "結束曦覽",
            Subtitle = "關閉程式（含系統匣圖示）",
            Kind = "動作", Icon = ActionIcon,
            Keywords = ["exit", "quit", "結束", "關閉", "離開"],
            Invoke = () => Application.Current.Shutdown(),
        });

        return list;
    }
}
