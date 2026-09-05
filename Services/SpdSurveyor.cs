namespace XinSpect;

/// <summary>一條讀到並解出來的模組。</summary>
/// <param name="Bus">讀它的是哪一條匯流排——這是事實的血統，畫面上要說得出來。</param>
public sealed record SpdDirectRead(string Bus, byte Address, byte[] Raw, SpdSnapshot Decoded);

/// <summary>一次全機 SPD 巡檢的結果。</summary>
/// <param name="Problems">有裝置卻讀不到、或型別不支援的位址。<b>這些是發現，不是空插槽。</b></param>
/// <param name="Notes">匯流排層級的說明，每一條都指名是哪一條匯流排。</param>
public sealed record SpdSurvey(IReadOnlyList<SpdDirectRead> Modules,
                              IReadOnlyList<SpdSlot> Problems,
                              IReadOnlyList<string> Notes);

/// <summary>
/// 把候選的每一條匯流排走一遍，收集所有讀得到的 SPD。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要試多條：主流桌上平台的 DIMM SPD 在 PCH 的 SMBus 上，HEDT／伺服器平台在處理器
/// 記憶體控制器自己的 SMBus 上（而且是兩組）。哪一條有東西是platform決定的，不是使用者該操心的，
/// 所以這裡逐條試、把結果併起來，並且<b>每一筆事實都記下它是從哪一條讀到的</b>。
/// </para>
/// <para>
/// 取不到某一條匯流排不是致命錯誤——記一句話繼續下一條。整機一條都讀不到時，
/// <see cref="SpdSurvey.Notes"/> 裡會有每一條各自的原因，而不是一句沒用的「讀取失敗」。
/// </para>
/// </remarks>
public static class SpdSurveyor
{
    /// <summary>呼叫端負責先取得軟體層的匯流排鎖（<see cref="SmbusBusLock"/>）並在結束後釋放。</summary>
    public static SpdSurvey Survey(IEnumerable<ISpdBus> buses)
    {
        var modules = new List<SpdDirectRead>();
        var problems = new List<SpdSlot>();
        var notes = new List<string>();

        foreach (var bus in buses)
        {
            if (!bus.TryAcquireBus(out string reason))
            {
                notes.Add($"{bus.Description}：{reason}");
                continue;
            }

            try
            {
                var scan = SpdReader.ReadAll(bus);
                if (scan.BusNote.Length > 0) notes.Add($"{bus.Description}：{scan.BusNote}");

                foreach (var slot in scan.Slots)
                {
                    if (slot.Kind == SpdKind.Empty) continue;

                    if (slot.Kind == SpdKind.Ddr4 && slot.Raw is not null
                        && SpdDecoder.Decode(slot.Raw) is { } decoded)
                    {
                        modules.Add(new SpdDirectRead(bus.Description, slot.Address, slot.Raw, decoded));
                        continue;
                    }
                    problems.Add(slot);
                }
            }
            finally
            {
                bus.ReleaseBus();
            }
        }

        return new SpdSurvey(modules, problems, notes);
    }
}

/// <summary>
/// 列出這台機器上所有可能掛著 SPD 的匯流排。
/// </summary>
/// <remarks>
/// 這一層薄到不需要測試（照本專案的慣例：特權存取層保持極薄，驗證力量放在純函式與狀態機上）。
/// 順序是先 PCH 再處理器 iMC，因為前者的存取代價與風險都低一級。
/// </remarks>
public static class SpdBusFactory
{
    public static List<ISpdBus> Candidates(WinRing0Bridge bridge, List<string> notes)
    {
        var buses = new List<ISpdBus>();

        if (bridge.IoPortAvailable)
        {
            var pch = SmbusDiscovery.Find((b, d, f, r) => bridge.ReadPciConfig(b, d, f, r), out string note);
            if (pch is not null) buses.Add(new SmbusController(new WinRing0SmbusIo(bridge), pch.IoBase));
            else notes.Add(note);
        }
        else
        {
            notes.Add("PCH SMBus：I/O 埠存取不可用，" + bridge.Error);
        }

        if (bridge.PciWriteAvailable)
        {
            var pci = new WinRing0PciConfig(bridge);
            var imc = ImcSmbusDiscovery.Find(pci, out string note);
            foreach (var loc in imc) buses.Add(new ImcSmbusController(pci, loc));
            if (imc.Count == 0) notes.Add(note);
        }
        else
        {
            notes.Add("處理器 iMC SMBus：PCI 設定空間寫入不可用，" + bridge.Error);
        }

        return buses;
    }
}
