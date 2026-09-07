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
    public const string PluginVersion = "0.1.11-dev";
    private const string BridgePrefix = "http://127.0.0.1:18946/";
    private HttpListener _listener;
    private CancellationTokenSource _stopToken;
    private readonly ConcurrentQueue<PendingGameAction> _pendingGameActions = new();
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
                if (!string.IsNullOrWhiteSpace(payload.Action))
                {
                    var amount = ReadAmount(payload.Parameters);
                    if (string.Equals(payload.Action, "add_money", StringComparison.OrdinalIgnoreCase) && amount == 0) amount = 100;
                    if (string.Equals(payload.Action, "remove_money", StringComparison.OrdinalIgnoreCase)) amount = -Math.Abs(amount == 0 ? 100 : amount);
                    gameplay = QueueGameAction(payload.Action, Math.Clamp(amount, -100000, 100000), out resultMessage);
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

    private static int ReadAmount(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("amount", out var value)) return 0;
        return value.TryGetInt32(out var parsed) ? Math.Clamp(Math.Abs(parsed), 1, 100000) : 0;
    }

    private bool QueueGameAction(string action, int amount, out string message)
    {
        var pending = new PendingGameAction(action, amount);
        _pendingGameActions.Enqueue(pending);
        if (!pending.Completion.Task.Wait(TimeSpan.FromSeconds(10)))
        {
            message = $"{action} non exécuté : délai dépassé en attendant le thread principal Unity";
            Log.LogWarning(message);
            return false;
        }

        var result = pending.Completion.Task.Result;
        message = result.Message;
        return result.Success;
    }

    private void ProcessPendingActions()
    {
        while (_pendingGameActions.TryDequeue(out var pending))
        {
            try
            {
                var success = TryGameAction(pending.Action, pending.Amount, out var message);
                pending.Completion.TrySetResult(new ActionResult(success, message));
            }
            catch (Exception ex)
            {
                pending.Completion.TrySetResult(new ActionResult(false, $"{pending.Action} non exécuté : {ex.GetBaseException().Message}"));
            }
        }
    }

    private bool TryGameAction(string action, int amount, out string message)
    {
        if (string.Equals(action, "add_money", StringComparison.OrdinalIgnoreCase))
            return TryAddMoney(Math.Abs(amount), out message);
        if (string.Equals(action, "remove_money", StringComparison.OrdinalIgnoreCase))
            return TryAddMoney(-Math.Abs(amount), out message);

        var mappings = new Dictionary<string, (string type, string[] methods, object[] args)>(StringComparer.OrdinalIgnoreCase)
        {
            ["spawn_customer"] = ("CustomerManager", new[] { "SpawnCustomer" }, Array.Empty<object>()),
            ["spawn_shoplifter"] = ("CustomerManager", new[] { "SpawnShoplifter" }, Array.Empty<object>()),
            ["spawn_garbage"] = ("GarbageManager", new[] { "SpawnGarbage", "CreateJustGarbage" }, Array.Empty<object>()),
            ["spawn_mud"] = ("GarbageManager", new[] { "CreateJustDirt" }, Array.Empty<object>()),
            ["clean_store"] = ("GarbageManager", new[] { "Dusting" }, Array.Empty<object>()),
            ["upgrade_store"] = ("StoreLevelManager", new[] { "AddPoint" }, new object[] { 100 }),
        };

        var repeat = Math.Clamp(Math.Abs(amount), 1, 10);
        if (string.Equals(action, "spawn_customers", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 0; i < repeat; i++) if (!TryInvokeNamed("CustomerManager", new[] { "SpawnCustomer" }, Array.Empty<object>(), out message)) return false;
            message = $"{repeat} clients ajoutés";
            return true;
        }
        if (string.Equals(action, "spawn_shoplifters", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 0; i < repeat; i++) if (!TryInvokeNamed("CustomerManager", new[] { "SpawnShoplifter" }, Array.Empty<object>(), out message)) return false;
            message = $"{repeat} voleurs ajoutés";
            return true;
        }
        if (string.Equals(action, "spawn_delivery", StringComparison.OrdinalIgnoreCase) || string.Equals(action, "stock_bonus", StringComparison.OrdinalIgnoreCase))
            return TryDeliverUnlockedProducts(repeat, out message);
        if (string.Equals(action, "remove_customer", StringComparison.OrdinalIgnoreCase) || string.Equals(action, "remove_customers", StringComparison.OrdinalIgnoreCase))
            return TryRemoveCustomers(repeat, out message);
        if (string.Equals(action, "angry_customer", StringComparison.OrdinalIgnoreCase))
            return TryInvokeNamed("CustomerManager", new[] { "SpawnShoplifter" }, Array.Empty<object>(), out message);

        if (mappings.TryGetValue(action, out var mapping) && TryInvokeNamed(mapping.type, mapping.methods, mapping.args, out message))
            return true;

        if (string.Equals(action, "lights_off", StringComparison.OrdinalIgnoreCase) &&
            TrySetProperty("StoreLightManager", "TurnOn", false, out message)) return true;
        if (string.Equals(action, "lights_on", StringComparison.OrdinalIgnoreCase) &&
            TrySetProperty("StoreLightManager", "TurnOn", true, out message)) return true;
        if (string.Equals(action, "open_store", StringComparison.OrdinalIgnoreCase))
        {
            TryInvokeNamed("StoreStatus", new[] { "SetIsOpenField" }, new object[] { true }, out _);
            return TrySetProperty("StoreLightManager", "TurnOn", true, out message);
        }
        if (string.Equals(action, "close_store", StringComparison.OrdinalIgnoreCase) &&
            TryInvokeNamed("StoreStatus", new[] { "SetIsOpenField" }, new object[] { false }, out message)) return true;
        if (string.Equals(action, "block_checkout", StringComparison.OrdinalIgnoreCase) &&
            TryInvokeNamed("StoreStatus", new[] { "SetIsOpenField" }, new object[] { false }, out message)) return true;
        if (string.Equals(action, "announce_donor", StringComparison.OrdinalIgnoreCase))
        {
            message = "Action annoncee dans le journal du pont";
            return true;
        }

        message = $"{action} non exécuté : action non disponible dans cette version du jeu";
        Log.LogWarning(message);
        return false;
    }

    private bool TryDeliverUnlockedProducts(int count, out string message)
    {
        try
        {
            var deliveryType = FindType("DeliveryManager");
            var licenseType = FindType("ProductLicenseManager");
            var cartType = FindType("CartData");
            var itemType = FindType("ItemQuantity");
            var delivery = deliveryType is null ? null : GetSingleton(deliveryType) ?? FindUnityInstance(deliveryType);
            var licenses = licenseType is null ? null : GetSingleton(licenseType) ?? FindUnityInstance(licenseType);
            if (delivery is null || licenses is null || cartType is null || itemType is null) { message = "système de livraison ou licences introuvable"; return false; }
            var unlockedProperty = licenseType.GetProperty("UnlockedProducts", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var activeProperty = licenseType.GetProperty("ActiveProducts", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var productPool = ReadListItems(unlockedProperty?.GetValue(licenses)).Select(Convert.ToInt32).Where(id => id > 0).Distinct().ToList();
            if (productPool.Count == 0)
                productPool = ReadListItems(activeProperty?.GetValue(licenses)).Select(Convert.ToInt32).Where(id => id > 0).Distinct().ToList();
            if (productPool.Count == 0) { message = "aucun article débloqué disponible pour la livraison"; return false; }
            var cartsProperty = cartType.GetProperty("ProductInCarts", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var cartsType = cartsProperty?.PropertyType;
            if (cartsType is null || cartsProperty?.CanWrite != true) { message = "conteneur de livraison introuvable"; return false; }
            var method = deliveryType.GetMethod("Delivery", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { cartType }, null);
            if (method is null) { message = "méthode Delivery introuvable"; return false; }
            var deliveredProducts = new List<int>();
            for (var i = 0; i < count; i++)
            {
                var productId = productPool[UnityEngine.Random.Range(0, productPool.Count)];
                var cart = Activator.CreateInstance(cartType);
                var item = Activator.CreateInstance(itemType, new object[] { productId, 0f });
                var carts = Activator.CreateInstance(cartsType);
                var add = carts?.GetType().GetMethod("Add", new[] { itemType });
                if (cart is null || item is null || carts is null || add is null) { message = "conteneur de livraison introuvable"; return false; }
                add.Invoke(carts, new[] { item });
                cartsProperty.SetValue(cart, carts);
                method.Invoke(delivery, new[] { cart });
                deliveredProducts.Add(productId);
            }
            message = $"{count} livraison(s) aléatoire(s) parmi {productPool.Count} article(s) débloqué(s)";
            return true;
        }
        catch (Exception ex) { message = $"livraison non exécutée : {ex.GetBaseException().Message}"; return false; }
    }

    private bool TryRemoveCustomers(int count, out string message)
    {
        try
        {
            var managerType = FindType("CustomerManager");
            var generatorType = FindType("CustomerGenerator");
            var manager = managerType is null ? null : GetSingleton(managerType) ?? FindUnityInstance(managerType);
            var generator = generatorType is null ? null : GetSingleton(generatorType) ?? FindUnityInstance(generatorType);
            var customers = managerType?.GetProperty("ActiveCustomers", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(manager);
            var customerList = ReadListItems(customers).Take(count).ToList();
            var method = generatorType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault(m => m.Name == "DeSpawn" && m.GetParameters().Length == 1);
            if (generator is null || method is null || customerList.Count == 0) { message = "aucun client actif à retirer"; return false; }
            foreach (var customer in customerList) method.Invoke(generator, new[] { customer });
            message = $"{customerList.Count} client(s) retiré(s)";
            return true;
        }
        catch (Exception ex) { message = $"retrait de client impossible : {ex.GetBaseException().Message}"; return false; }
    }

    private static Type FindType(string typeName) => AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeGetTypes).FirstOrDefault(type => string.Equals(type.Name, typeName, StringComparison.Ordinal));

    private static IEnumerable<object> ReadListItems(object list)
    {
        if (list is null) yield break;
        var listType = list.GetType();
        var countProperty = listType.GetProperty("Count", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var itemProperty = listType.GetProperty("Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (countProperty is null || itemProperty is null) yield break;
        var count = Convert.ToInt32(countProperty.GetValue(list));
        for (var index = 0; index < count; index++)
        {
            object item;
            try { item = itemProperty.GetValue(list, new object[] { index }); }
            catch { yield break; }
            if (item is not null) yield return item;
        }
    }

    private bool TryInvokeNamed(string typeName, string[] methodNames, object[] args, out string message)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeGetTypes)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, typeName, StringComparison.Ordinal));
        if (type is null) { message = $"type {typeName} introuvable"; return false; }
        var target = GetSingleton(type) ?? FindUnityInstance(type);
        if (target is null) { message = $"instance de {typeName} introuvable"; return false; }
        var method = methodNames.Select(name => SafeGetMethods(type).FirstOrDefault(candidate =>
            candidate.Name == name && !candidate.IsStatic && candidate.GetParameters().Length == args.Length))
            .FirstOrDefault(candidate => candidate is not null);
        if (method is null) { message = $"méthode de {typeName} introuvable"; return false; }
        method.Invoke(target, args);
        message = $"{method.Name} exécuté";
        return true;
    }

    private bool TrySetProperty(string typeName, string propertyName, object value, out string message)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeGetTypes)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, typeName, StringComparison.Ordinal));
        var target = type is null ? null : GetSingleton(type) ?? FindUnityInstance(type);
        var property = type?.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (target is null || property?.CanWrite != true) { message = $"propriété {typeName}.{propertyName} introuvable"; return false; }
        property.SetValue(target, value);
        message = $"{propertyName} défini sur {value}";
        return true;
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
                    message = $"MoneyTransition exécuté par le jeu : {(amount >= 0 ? "+" : "")}{amount}";
                    return true;
                }

                var moneyProperty = moneyManagerType.GetProperty("Money", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (moneyManager is not null && moneyProperty?.CanRead == true && moneyProperty.CanWrite)
                {
                    var current = Convert.ToSingle(moneyProperty.GetValue(moneyManager));
                    var updated = current + amount;
                    var targetType = Nullable.GetUnderlyingType(moneyProperty.PropertyType) ?? moneyProperty.PropertyType;
                    moneyProperty.SetValue(moneyManager, Convert.ChangeType(updated, targetType));
                    message = $"Money exécuté par le jeu : {(amount >= 0 ? "+" : "")}{amount}";
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

    private sealed class PendingGameAction
    {
        public PendingGameAction(string action, int amount) { Action = action; Amount = amount; }
        public string Action { get; }
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
