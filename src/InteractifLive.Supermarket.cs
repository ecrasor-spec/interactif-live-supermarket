#nullable disable

using BepInEx;
using BepInEx.Unity.IL2CPP;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using UnityEngine;

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public NullableAttribute(byte value) { }
        public NullableAttribute(byte[] value) { }
    }

    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public NullableContextAttribute(byte value) { }
    }
}

namespace InteractifLive.Supermarket
{

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "jesink.interactiflive.supermarket";
    public const string PluginName = "Interactif Live - Supermarket Simulator";
    public const string PluginVersion = "0.1.6-dev";
    private const string BridgePrefix = "http://127.0.0.1:18946/";
    private HttpListener _listener;
    private CancellationTokenSource _stopToken;
    private readonly ConcurrentQueue<PendingMoneyAction> _pendingMoneyActions = new();
    private ActionRunner _actionRunner;

    public override void Load()
    {
        Log.LogInfo($"{PluginName} {PluginVersion} chargé.");
        _actionRunner = AddComponent<ActionRunner>();
        _actionRunner.Plugin = this;
        StartBridge();
    }

    public override bool Unload()
    {
        _stopToken?.Cancel();
        _listener?.Close();
        _listener = null;
        _actionRunner = null;
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
                    gameplay = QueueAddMoney(Math.Clamp(amount, 1, 100000), out resultMessage);
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

    private bool QueueAddMoney(int amount, out string message)
    {
        var pending = new PendingMoneyAction(amount);
        _pendingMoneyActions.Enqueue(pending);
        if (!pending.Completion.Task.Wait(TimeSpan.FromSeconds(10)))
        {
            message = "AddMoney non exécuté : délai dépassé en attendant le thread principal Unity";
            Log.LogWarning(message);
            return false;
        }

        var result = pending.Completion.Task.Result;
        message = result.Message;
        return result.Success;
    }

    private void ProcessPendingActions()
    {
        while (_pendingMoneyActions.TryDequeue(out var pending))
        {
            try
            {
                var success = TryAddMoney(pending.Amount, out var message);
                pending.Completion.TrySetResult(new ActionResult(success, message));
            }
            catch (Exception ex)
            {
                pending.Completion.TrySetResult(new ActionResult(false, $"AddMoney non exécuté : {ex.GetBaseException().Message}"));
            }
        }
    }

    private bool TryAddMoney(int amount, out string message)
    {
        try
        {
            var moneyManagerType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .FirstOrDefault(type => string.Equals(type.Name, "MoneyManager", StringComparison.Ordinal));
            if (moneyManagerType is not null)
            {
                var moneyManager = GetSingleton(moneyManagerType) ?? FindUnityInstance(moneyManagerType);
                var moneyTransition = moneyManagerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == "MoneyTransition" && method.GetParameters().Length == 3);
                if (moneyManager is not null && moneyTransition is not null)
                {
                    var transitionType = moneyTransition.GetParameters()[1].ParameterType;
                    moneyTransition.Invoke(moneyManager, new object[] { (float)amount, Enum.ToObject(transitionType, 0), true });
                    message = $"AddMoney() exécuté par le jeu : +{amount}";
                    return true;
                }

                var moneyProperty = moneyManagerType.GetProperty("Money", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (moneyManager is not null && moneyProperty?.CanRead == true && moneyProperty.CanWrite)
                {
                    var current = Convert.ToSingle(moneyProperty.GetValue(moneyManager));
                    var updated = current + amount;
                    var targetType = Nullable.GetUnderlyingType(moneyProperty.PropertyType) ?? moneyProperty.PropertyType;
                    moneyProperty.SetValue(moneyManager, Convert.ChangeType(updated, targetType));
                    message = $"AddMoney() exécuté par le jeu : +{amount}";
                    return true;
                }
            }

            var methodOwners = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .SelectMany(type => SafeGetMethods(type).Select(method => new { type, method }))
                .Where(item => item.method.Name == "AddMoney" && item.method.GetParameters().Length == 0)
                .Where(item => !string.Equals(item.type.FullName, "__Project__.Scripts.Cheating.CheatCanvas", StringComparison.Ordinal))
                .Where(item => item.type.FullName is null || !item.type.FullName.Contains("Cheat", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var methodOwner = methodOwners.FirstOrDefault();
            if (methodOwner is null) { message = "MoneyManager.Money introuvable ou non modifiable"; return false; }

            var target = GetSingleton(methodOwner.type) ?? FindUnityInstance(methodOwner.type);
            if (!methodOwner.method.IsStatic && target is null)
            {
                message = $"AddMoney non exécuté : instance de {methodOwner.type.FullName} introuvable";
                Log.LogWarning(message);
                return false;
            }
            methodOwner.method.Invoke(methodOwner.method.IsStatic ? null : target, null);
            message = $"AddMoney() exécuté par le jeu : +{amount}";
            return true;
        }
        catch (Exception ex)
        {
            message = $"AddMoney non exécuté : {ex.GetBaseException().Message}";
            Log.LogWarning(message);
            return false;
        }
    }

    private static object GetSingleton(Type type)
    {
        foreach (var name in new[] { "Instance", "instance", "CurrentInstance" })
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (property is not null) return property.GetValue(null);
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (field is not null) return field.GetValue(null);
        }
        return null;
    }

    private object FindUnityInstance(Type type)
    {
        try
        {
            var unityObjectType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UnityEngine.Object", throwOnError: false))
                .FirstOrDefault(type => type is not null);
            if (unityObjectType is null) return null;

            var findMethods = unityObjectType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == "FindObjectOfType"
                    && method.IsGenericMethodDefinition
                    && method.GetGenericArguments().Length == 1
                    && (method.GetParameters().Length == 0
                        || (method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(bool))))
                .OrderByDescending(method => method.GetParameters().Length == 1)
                .ToList();

            foreach (var findMethod in findMethods)
            {
                var arguments = findMethod.GetParameters().Length == 1 ? new object[] { true } : null;
                var result = findMethod.MakeGenericMethod(type).Invoke(null, arguments);
                if (result is not null) return result;
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Recherche Unity de l'instance {type.FullName} impossible : {ex.GetBaseException().Message}");
            return null;
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(type => type is not null)!; }
        catch { return Array.Empty<Type>(); }
    }

    private static IEnumerable<MethodInfo> SafeGetMethods(Type type)
    {
        try { return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static); }
        catch { return Array.Empty<MethodInfo>(); }
    }

    private sealed class BridgeAction
    {
        public string Action { get; set; }
        public string Donor { get; set; }
        public JsonElement Parameters { get; set; }
    }

    private sealed class PendingMoneyAction
    {
        public PendingMoneyAction(int amount) => Amount = amount;
        public int Amount { get; }
        public TaskCompletionSource<ActionResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ActionResult
    {
        public ActionResult(bool success, string message)
        {
            Success = success;
            Message = message;
        }

        public bool Success { get; }
        public string Message { get; }
    }

    private sealed class ActionRunner : MonoBehaviour
    {
        public Plugin Plugin { get; set; }

        public void Update()
        {
            Plugin?.ProcessPendingActions();
        }
    }
}
}
