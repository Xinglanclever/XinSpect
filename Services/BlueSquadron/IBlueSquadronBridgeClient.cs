using System.Text.Json;

namespace XinSpect;

// Managed seam: lifecycle tests never extract a bridge, launch a process or touch a driver.
internal interface IBlueSquadronBridgeClient : IDisposable
{
    BlueSquadronEngine? NativeEngine { get; }
    bool Connected { get; }
    string LastError { get; }
    event Action<JsonElement>? AlertReceived;
    bool Start();
    JsonElement? SendCmd(string cmd, int timeoutMs);
    JsonElement? SendCmd(string cmd, Dictionary<string, object> extra, int timeoutMs);
}

internal sealed class NativeBlueSquadronBridgeClient : IBlueSquadronBridgeClient
{
    private readonly BlueSquadronEngine _engine = new();
    public BlueSquadronEngine NativeEngine => _engine;
    public bool Connected => _engine.Connected;
    public string LastError => _engine.LastError;
    public event Action<JsonElement>? AlertReceived
    {
        add => _engine.AlertReceived += value;
        remove => _engine.AlertReceived -= value;
    }
    public bool Start() => _engine.Start();
    public JsonElement? SendCmd(string cmd, int timeoutMs) => _engine.SendCmd(cmd, timeoutMs);
    public JsonElement? SendCmd(string cmd, Dictionary<string, object> extra, int timeoutMs)
        => _engine.SendCmd(cmd, extra, timeoutMs);
    public void Dispose() => _engine.Dispose();
}
