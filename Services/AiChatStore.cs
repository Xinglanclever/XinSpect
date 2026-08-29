using System.IO;
using System.Text.Json;

namespace XinSpect;

/// <summary>
/// AI 對話的本機保存：把聊天內容寫成 %APPDATA%\XinSpect\aichat.json，下次啟動可接續。
/// 只存在本機、只存純文字（角色與內容），不含任何金鑰；使用者可於設定關閉，關閉時會一併刪檔。
/// </summary>
public sealed class AiChatStore
{
    /// <summary>最多保留的訊息筆數（含工具查詢紀錄），避免檔案無限成長。</summary>
    public const int MaxRows = 240;

    private readonly string _file;

    public AiChatStore(string? folder = null)
    {
        string dir = folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect");
        _file = Path.Combine(dir, "aichat.json");
    }

    /// <summary>檔案完整路徑（供設定頁顯示或測試斷言）。</summary>
    public string FilePath => _file;

    private sealed class Row
    {
        public bool User { get; set; }
        public bool Tool { get; set; }
        public string? Text { get; set; }
    }

    /// <summary>讀回上次的對話；檔案不存在或毀損時回傳空清單（不視為錯誤）。</summary>
    public List<AiMessage> Load()
    {
        var list = new List<AiMessage>();
        try
        {
            if (!File.Exists(_file)) return list;
            var rows = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(_file));
            if (rows is null) return list;
            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(r.Text)) continue;
                list.Add(new AiMessage { IsUser = r.User, IsTool = r.Tool, Text = r.Text });
            }
        }
        catch { /* 毀損就當作沒有歷史，不影響本次對話 */ }
        return list;
    }

    /// <summary>寫入對話（只保留最後 <see cref="MaxRows"/> 筆）。寫檔失敗不影響執行期對話。</summary>
    public void Save(IEnumerable<AiMessage> messages)
    {
        try
        {
            var rows = messages
                .Where(m => !string.IsNullOrWhiteSpace(m.Text))
                .Select(m => new Row { User = m.IsUser, Tool = m.IsTool, Text = m.Text })
                .ToList();
            if (rows.Count > MaxRows) rows = rows.Skip(rows.Count - MaxRows).ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 權限或磁碟問題：保存是加值功能，失敗不打斷對話 */ }
    }

    /// <summary>刪除保存檔（清除對話或關閉「保留對話」時呼叫）。</summary>
    public void Delete()
    {
        try { if (File.Exists(_file)) File.Delete(_file); }
        catch { /* 刪不掉（被佔用）時下次覆寫即可 */ }
    }
}
