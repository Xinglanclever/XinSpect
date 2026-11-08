using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XinSpect;

/// <summary>
/// Optional, user-supplied CPU/GPU history catalog. The app does not ship workbook-derived data.
/// Import is explicit so licensing and redistribution decisions remain with the user.
/// </summary>
public sealed class HardwareCatalogService
{
    private readonly object _gate = new();
    private CatalogPayload? _payload;
    private string? _error;
    private string? _loadedPath;
    private readonly bool _discoverUserCatalog;

    public HardwareCatalogService(bool discoverUserCatalog = true)
    {
        _discoverUserCatalog = discoverUserCatalog;
    }

    public string Status => _payload is not null
        ? $"已載入 {_payload.Records.Count} 筆 CPU/GPU 歷史型號（來源：{Path.GetFileName(_loadedPath)})"
        : _error is null ? "尚未匯入型號索引" : "型號索引無法載入：" + _error;

    public string? LoadedPath => _loadedPath;

    public IReadOnlyList<HardwareCatalogRecord> Search(string? query, string? kind = null, int? year = null, int? limit = null)
    {
        EnsureLoaded();
        if (_payload is null) return [];
        query = query?.Trim();
        var filtered = _payload.Records.Where(r =>
            (string.IsNullOrWhiteSpace(kind) || string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase)) &&
            (!year.HasValue || r.Year == year.Value) &&
            (string.IsNullOrWhiteSpace(query) || r.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)));
        return (limit.HasValue ? filtered.Take(Math.Clamp(limit.Value, 1, 5000)) : filtered).ToList();
    }

    public void LoadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("未指定檔案", nameof(path));
        var fullPath = Path.GetFullPath(path);
        var json = File.ReadAllText(fullPath);
        var payload = JsonSerializer.Deserialize<CatalogPayload>(json, JsonOptions)
                      ?? throw new InvalidDataException("索引內容為空");
        if (payload.Records.Count == 0) throw new InvalidDataException("索引沒有 CPU/GPU 記錄");
        lock (_gate)
        {
            _payload = payload;
            _loadedPath = fullPath;
            _error = null;
        }
    }

    /// <summary>非同步匯入：讀檔與 JSON 解析在背景執行緒（P3-22：原版同步在 UI 執行緒，
    /// 大索引會凍住視窗）。結果語意與 <see cref="LoadFile"/> 完全一致；例外原樣拋給呼叫端。</summary>
    public async Task LoadFileAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("未指定檔案", nameof(path));
        var fullPath = Path.GetFullPath(path);
        var payload = await Task.Run(() =>
        {
            var json = File.ReadAllText(fullPath);
            return JsonSerializer.Deserialize<CatalogPayload>(json, JsonOptions);
        }, ct) ?? throw new InvalidDataException("索引內容為空");
        if (payload.Records.Count == 0) throw new InvalidDataException("索引沒有 CPU/GPU 記錄");
        lock (_gate)
        {
            _payload = payload;
            _loadedPath = fullPath;
            _error = null;
        }
    }

    public string Attribution
    {
        get { EnsureLoaded(); return _payload?.Attribution ?? "尚未匯入使用者提供的資料"; }
    }

    public string Limitations
    {
        get { EnsureLoaded(); return _payload?.Limitations ?? "本程式不內建或再散布 workbook 衍生資料；請先確認資料授權後再匯入。"; }
    }

    private void EnsureLoaded()
    {
        if (_payload is not null || _error is not null) return;
        lock (_gate)
        {
            if (_payload is not null || _error is not null || !_discoverUserCatalog) return;
            var configured = Environment.GetEnvironmentVariable("XINSPECT_CPU_GPU_CATALOG");
            var userPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XinSpect", "catalogs", "cpu-gpu-catalog.json");
            var path = !string.IsNullOrWhiteSpace(configured) ? configured : userPath;
            if (!File.Exists(path)) return;
            try { LoadFile(path); }
            catch (Exception ex) { _error = ex.Message; }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class CatalogPayload
    {
        public string Attribution { get; set; } = "";
        public string Limitations { get; set; } = "";
        public List<HardwareCatalogRecord> Records { get; set; } = [];
    }
}

public sealed class HardwareCatalogRecord
{
    public string Kind { get; set; } = "";
    public string? Vendor { get; set; }
    public string? Model { get; set; }
    public string? Family { get; set; }
    public string? Segment { get; set; }
    public int? Year { get; set; }
    public string? Architecture { get; set; }
    public string? Process { get; set; }
    public string? Socket { get; set; }
    public string? Cores { get; set; }
    public string? Threads { get; set; }
    public string? Tdp { get; set; }
    public string? Status { get; set; }
    public string? Evidence { get; set; }
    public string? Notes { get; set; }
    public string? SourceUrl { get; set; }
    public string? SourceRef { get; set; }
    public string? GpuChip { get; set; }
    public string? Memory { get; set; }
    public string? Interface { get; set; }

    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public string SearchText => string.Join(" ", new[] { Kind, Vendor, Model, Family, Segment, Architecture, Process, Socket, Status, Evidence, Notes, GpuChip, Memory, Interface }.Concat(Extra?.Values.Select(v => v.ToString()) ?? []).Where(s => !string.IsNullOrWhiteSpace(s)));
    public string DisplayName => string.IsNullOrWhiteSpace(Model) ? "未命名型號" : Model!;
    public string Summary => string.Join(" ・ ", new[] { Vendor, Family, Segment, Year?.ToString(), Architecture, Process, Socket, GpuChip, Memory, Interface, Cores is null ? null : $"{Cores} 核", Threads is null ? null : $"{Threads} 執行緒", Tdp }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string EvidenceText => string.IsNullOrWhiteSpace(Evidence) ? "證據等級：未提供" : "證據等級：" + Evidence;
    public string StatusText => string.IsNullOrWhiteSpace(Status) ? "狀態：未提供" : "狀態：" + Status;
    public string NotesText => string.IsNullOrWhiteSpace(Notes) ? "備註：未提供" : "備註：" + Notes;
    public string SourceText => string.Join(" ・ ", new[] { SourceRef, SourceUrl }.Where(s => !string.IsNullOrWhiteSpace(s)));
}
