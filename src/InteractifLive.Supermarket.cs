using BepInEx;
using BepInEx.Unity.IL2CPP;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace InteractifLive.Supermarket;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "jesink.interactiflive.supermarket";
    public const string PluginName = "Interactif Live - Supermarket Simulator";
    public const string PluginVersion = "0.1.0-dev";
    private const string BridgePrefix = "http://127.0.0.1:18946/";
    private HttpListener? _listener;
    private CancellationTokenSource? _stopToken;

    public override void Load()
    {
        Log.LogInfo($"{PluginName} {PluginVersion} chargé.");
        StartBridge();
    }

    public override bool Unload()
    {
        _stopToken?.Cancel();
        _listener?.Close();
        _listener = null;
        return true;
    }

    private void StartBridge()
    {
        try
        {
            _stopToken = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add(BridgePrefix);
            _listener.Start();
            _ = Task.Run(() => ListenLoop(_stopToken.Token));
            Log.LogInfo($"Pont local actif sur {BridgePrefix}");
        }
        catch (Exception ex)
        {
            Log.LogError($"Impossible de démarrer le pont local : {ex.Message}");
        }
    }

    private async Task ListenLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { IsListening: true })
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
                _ = Task.Run(() => HandleRequest(context), cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex) { Log.LogWarning($"Erreur du pont local : {ex.Message}"); }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            response.Headers["Access-Control-Allow-Origin"] = "http://127.0.0.1";
            response.ContentType = "application/json; charset=utf-8";
            string body;
            if (request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/health")
            {
                body = JsonSerializer.Serialize(new { success = true, plugin = PluginGuid, version = PluginVersion, ready = true });
            }
            else if (request.HttpMethod == "POST" && request.Url?.AbsolutePath == "/action")
            {
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                var payload = JsonSerializer.Deserialize<BridgeAction>(reader.ReadToEnd()) ?? new BridgeAction();
                Log.LogInfo($"Action reçue : {payload.Action ?? "ping"} · donateur : {payload.Donor ?? "inconnu"}");
                body = JsonSerializer.Serialize(new { success = true, accepted = true, action = payload.Action ?? "ping", gameplay = false });
            }
            else
            {
                response.StatusCode = 404;
                body = JsonSerializer.Serialize(new { success = false, error = "Route inconnue" });
            }
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Requête du pont refusée : {ex.Message}");
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
        }
    }

    private sealed class BridgeAction
    {
        public string? Action { get; set; }
        public string? Donor { get; set; }
        public JsonElement Parameters { get; set; }
    }
}
