using BepInEx;
using BepInEx.Unity.IL2CPP;
using System.Net;
using System.Reflection;
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
                var payload = JsonSerializer.Deserialize<BridgeAction>(reader.ReadToEnd(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new BridgeAction();
                Log.LogInfo($"Action reçue : {payload.Action ?? "ping"} · donateur : {payload.Donor ?? "inconnu"}");
                var gameplay = false;
                var resultMessage = "Action journalisée";
                if (string.Equals(payload.Action, "add_money", StringComparison.OrdinalIgnoreCase))
                {
                    var amount = payload.Parameters.ValueKind == JsonValueKind.Object && payload.Parameters.TryGetProperty("amount", out var value) && value.TryGetInt32(out var parsed) ? parsed : 100;
                    gameplay = TryAddMoney(Math.Clamp(amount, 1, 100000), out resultMessage);
                }
                body = JsonSerializer.Serialize(new { success = true, accepted = true, action = payload.Action ?? "ping", gameplay, message = resultMessage });
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

    private bool TryAddMoney(int amount, out string message)
    {
        try
        {
            var bankType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .FirstOrDefault(type => string.Equals(type.Name, "BankManager", StringComparison.Ordinal));
            if (bankType is null) { message = "BankManager introuvable"; return false; }

            var target = GetSingleton(bankType);
            var method = bankType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .FirstOrDefault(candidate => candidate.Name == "AddMoney" && candidate.GetParameters().Length == 0);
            if (method is null) { message = "Méthode AddMoney() introuvable"; return false; }
            method.Invoke(method.IsStatic ? null : target, null);
            message = "AddMoney() exécuté par le jeu";
            return true;
        }
        catch (Exception ex)
        {
            message = $"AddMoney non exécuté : {ex.GetBaseException().Message}";
            Log.LogWarning(message);
            return false;
        }
    }

    private static object? GetSingleton(Type type)
    {
        foreach (var name in new[] { "Instance", "instance", "CurrentInstance" })
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (property is not null) return property.GetValue(null);
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (field is not null) return field.GetValue(null);
        }
        return null;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(type => type is not null)!; }
        catch { return Array.Empty<Type>(); }
    }

    private sealed class BridgeAction
    {
        public string? Action { get; set; }
        public string? Donor { get; set; }
        public JsonElement Parameters { get; set; }
    }
}
