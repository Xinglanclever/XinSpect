using Microsoft.Win32;

namespace XinSpect;

/// <summary>單一顯示器的色域資訊（由 EDID 色度座標推算）。</summary>
public sealed class MonitorGamutInfo
{
    public string Name { get; init; } = "—";
    public string Manufacturer { get; init; } = "—";
    public string WhitePointText { get; init; } = "—";
    public string PrimariesText { get; init; } = "—";
    public string SrgbText { get; init; } = "—";
    public string AdobeText { get; init; } = "—";
    public string DciText { get; init; } = "—";
    public string AreaText { get; init; } = "—";
    public string Assessment { get; init; } = "—";
    public bool Valid { get; init; }
}

/// <summary>
/// 螢幕色域偵測：自登錄檔（HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY）讀取各顯示器的原始 EDID，
/// 解析 CIE 1931 色度座標（紅 / 綠 / 藍原色與白點），計算色域三角形，
/// 並以多邊形裁剪求得對 sRGB、Adobe RGB、DCI-P3 三種標準色域的覆蓋率。
/// </summary>
public static class EdidService
{
    // 標準色域原色（CIE 1931 xy）
    private static readonly (double x, double y)[] SRGB =
        { (0.640, 0.330), (0.300, 0.600), (0.150, 0.060) };
    private static readonly (double x, double y)[] ADOBE =
        { (0.640, 0.330), (0.210, 0.710), (0.150, 0.060) };
    private static readonly (double x, double y)[] DCIP3 =
        { (0.680, 0.320), (0.265, 0.690), (0.150, 0.060) };

    public static List<MonitorGamutInfo> Detect()
    {
        var list = new List<MonitorGamutInfo>();
        var seen = new HashSet<string>();

        try
        {
            using var display = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\DISPLAY");
            if (display is null) return list;

            foreach (var modelName in display.GetSubKeyNames())
            {
                using var model = display.OpenSubKey(modelName);
                if (model is null) continue;

                foreach (var instName in model.GetSubKeyNames())
                {
                    try
                    {
                        using var inst = model.OpenSubKey(instName);
                        using var dp = inst?.OpenSubKey("Device Parameters");
                        if (dp?.GetValue("EDID") is not byte[] edid || edid.Length < 128) continue;
                        if (!ValidHeader(edid)) continue;

                        var info = Parse(edid, modelName);
                        // 以製造商 + 名稱去重（同一顯示器可能於登錄檔留有多筆執行個體）
                        string key = info.Manufacturer + "|" + info.Name + "|" + info.PrimariesText;
                        if (seen.Add(key)) list.Add(info);
                    }
                    catch { /* 單一執行個體讀取失敗不影響其餘 */ }
                }
            }
        }
        catch { /* 無存取權或機碼不存在時回傳目前結果 */ }

        return list;
    }

    private static bool ValidHeader(byte[] e)
        => e[0] == 0x00 && e[1] == 0xFF && e[2] == 0xFF && e[3] == 0xFF
        && e[4] == 0xFF && e[5] == 0xFF && e[6] == 0xFF && e[7] == 0x00;

    private static MonitorGamutInfo Parse(byte[] e, string fallbackName)
    {
        string mfr = ManufacturerId(e);
        string name = MonitorName(e) ?? fallbackName;

        // 色度座標（每軸 10 位元：高 8 位元於 0x1B~0x22，低 2 位元於 0x19~0x1A）
        double Rx = Coord(e[27], e[25], 6), Ry = Coord(e[28], e[25], 4);
        double Gx = Coord(e[29], e[25], 2), Gy = Coord(e[30], e[25], 0);
        double Bx = Coord(e[31], e[26], 6), By = Coord(e[32], e[26], 4);
        double Wx = Coord(e[33], e[26], 2), Wy = Coord(e[34], e[26], 0);

        var disp = new[] { (Rx, Ry), (Gx, Gy), (Bx, By) };
        double dispArea = TriArea(disp);

        // 色度座標須落在合理範圍且三角形面積夠大，否則視為無效（部分 EDID 未填色度）
        bool valid = dispArea > 0.001 && Rx is > 0 and < 1 && Gy is > 0 and < 1 && By is > 0 and < 1;
        if (!valid)
        {
            return new MonitorGamutInfo
            {
                Name = name, Manufacturer = mfr, Valid = false,
                Assessment = "此顯示器的 EDID 未提供有效色度資訊，無法計算色域覆蓋率。",
            };
        }

        double covSrgb = Coverage(disp, SRGB);
        double covAdobe = Coverage(disp, ADOBE);
        double covDci = Coverage(disp, DCIP3);
        double areaRatio = dispArea / TriArea(SRGB) * 100;

        return new MonitorGamutInfo
        {
            Name = name,
            Manufacturer = mfr,
            Valid = true,
            WhitePointText = $"白點 ({Wx:0.000}, {Wy:0.000})",
            PrimariesText = $"R({Rx:0.000},{Ry:0.000})　G({Gx:0.000},{Gy:0.000})　B({Bx:0.000},{By:0.000})",
            SrgbText = $"{covSrgb:0} %",
            AdobeText = $"{covAdobe:0} %",
            DciText = $"{covDci:0} %",
            AreaText = $"色域面積約為 sRGB 的 {areaRatio:0} %",
            Assessment = Assess(covSrgb, covDci),
        };
    }

    private static string Assess(double srgb, double dci) => srgb switch
    {
        >= 99 when dci >= 90 => "廣色域面板：涵蓋 sRGB 並大幅涵蓋 DCI-P3，適合影像創作與 HDR 內容。",
        >= 95 => "涵蓋近乎完整的 sRGB，色彩表現良好，適合一般與多數創作用途。",
        >= 80 => "sRGB 覆蓋中等，日常使用足夠，專業色彩工作建議校色。",
        _ => "sRGB 覆蓋偏低，色彩範圍較窄，對色彩要求高的用途較不理想。",
    };

    // 由高 8 位元 + 低 2 位元組出 10 位元座標值（÷1024）
    private static double Coord(byte high, byte low, int shift)
        => ((high << 2) | ((low >> shift) & 0x3)) / 1024.0;

    private static string ManufacturerId(byte[] e)
    {
        int id = (e[8] << 8) | e[9];
        char c1 = (char)('A' + ((id >> 10) & 0x1F) - 1);
        char c2 = (char)('A' + ((id >> 5) & 0x1F) - 1);
        char c3 = (char)('A' + (id & 0x1F) - 1);
        string s = $"{c1}{c2}{c3}";
        return s.All(char.IsLetter) ? s : "—";
    }

    private static string? MonitorName(byte[] e)
    {
        // 四個 18 位元組描述子（位移 54/72/90/108）；標籤 0xFC 為顯示器名稱
        for (int off = 54; off <= 108; off += 18)
        {
            if (off + 18 > e.Length) break;
            if (e[off] == 0 && e[off + 1] == 0 && e[off + 2] == 0 && e[off + 3] == 0xFC)
            {
                var chars = new List<char>();
                for (int i = off + 5; i < off + 18; i++)
                {
                    byte b = e[i];
                    if (b == 0x0A) break;
                    if (b >= 0x20) chars.Add((char)b);
                }
                string name = new string(chars.ToArray()).Trim();
                if (name.Length > 0) return name;
            }
        }
        return null;
    }

    // ---- 幾何：三角形面積、多邊形裁剪求覆蓋率 --------------------------------

    private static double TriArea((double x, double y)[] t)
        => Math.Abs((t[1].x - t[0].x) * (t[2].y - t[0].y)
                  - (t[2].x - t[0].x) * (t[1].y - t[0].y)) / 2.0;

    /// <summary>覆蓋率＝顯示色域與參考色域交集面積 ÷ 參考色域面積（%）。</summary>
    private static double Coverage((double x, double y)[] disp, (double x, double y)[] reference)
    {
        var clip = EnsureCcw(disp);                      // 裁剪多邊形須為逆時針凸多邊形
        var inter = SutherlandHodgman(reference, clip);  // 以顯示三角形裁剪參考三角形
        double interArea = PolyArea(inter);
        double refArea = TriArea(reference);
        return refArea <= 0 ? 0 : Math.Clamp(interArea / refArea * 100, 0, 100);
    }

    // 三角形若為順時針則反轉為逆時針（Sutherland–Hodgman 的內側判定所需）
    private static (double x, double y)[] EnsureCcw((double x, double y)[] t)
    {
        double signed = (t[1].x - t[0].x) * (t[2].y - t[0].y)
                      - (t[2].x - t[0].x) * (t[1].y - t[0].y);
        return signed >= 0 ? t : new[] { t[0], t[2], t[1] };
    }

    /// <summary>Sutherland–Hodgman：以凸多邊形 <paramref name="clip"/> 裁剪 <paramref name="subject"/>。</summary>
    private static List<(double x, double y)> SutherlandHodgman((double x, double y)[] subject, (double x, double y)[] clip)
    {
        var output = new List<(double x, double y)>(subject);
        int n = clip.Length;
        for (int i = 0; i < n; i++)
        {
            var a = clip[i];
            var b = clip[(i + 1) % n];
            var input = output;
            output = new List<(double x, double y)>();
            if (input.Count == 0) break;

            for (int j = 0; j < input.Count; j++)
            {
                var cur = input[j];
                var prev = input[(j - 1 + input.Count) % input.Count];
                bool curIn = Inside(a, b, cur);
                bool prevIn = Inside(a, b, prev);
                if (curIn)
                {
                    if (!prevIn) output.Add(Intersect(prev, cur, a, b));
                    output.Add(cur);
                }
                else if (prevIn)
                {
                    output.Add(Intersect(prev, cur, a, b));
                }
            }
        }
        return output;
    }

    // 點 p 是否在有向邊 a→b 的左側（凸多邊形以逆時針排列時代表「內側」）
    private static bool Inside((double x, double y) a, (double x, double y) b, (double x, double y) p)
        => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x) >= -1e-12;

    private static (double x, double y) Intersect((double x, double y) p1, (double x, double y) p2,
                                                  (double x, double y) a, (double x, double y) b)
    {
        double a1 = b.y - a.y, b1 = a.x - b.x, c1 = a1 * a.x + b1 * a.y;
        double a2 = p2.y - p1.y, b2 = p1.x - p2.x, c2 = a2 * p1.x + b2 * p1.y;
        double det = a1 * b2 - a2 * b1;
        if (Math.Abs(det) < 1e-15) return p1;
        return ((b2 * c1 - b1 * c2) / det, (a1 * c2 - a2 * c1) / det);
    }

    private static double PolyArea(List<(double x, double y)> p)
    {
        if (p.Count < 3) return 0;
        double area = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            area += a.x * b.y - b.x * a.y;
        }
        return Math.Abs(area) / 2.0;
    }
}
