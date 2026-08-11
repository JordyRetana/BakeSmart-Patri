using BakeSmartPatri.Data;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace BakeSmartPatri.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly SqlStore _sqlStore;

    public ChatController(IHttpClientFactory httpClientFactory, IConfiguration config, SqlStore sqlStore)
    {
        _http = httpClientFactory.CreateClient();
        _config = config;
        _sqlStore = sqlStore;
    }

    public sealed record ChatRequest(string Message, IReadOnlyList<ChatMessage>? History = null, string? Page = null);
    public sealed record ChatMessage(string Role, string Content);

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ChatRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Message))
            return BadRequest(new { message = "Escriba un mensaje para el asistente." });

        var apiKey = _config["Groq:ApiKey"] ?? _config["GROQ_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return BadRequest(new { message = "Falta configurar la API key del bot." });

        var databaseContext = await BuildDatabaseContextAsync();
        var userContext = await BuildUserContextAsync();
        var systemPrompt = $"""
            Sos Richie, el asistente virtual pastelero de BakeSmart Patri, una reposteria en Costa Rica.
            Tu personalidad: amable, dulce, clara y profesional, como alguien que atiende una vitrina de queques, cupcakes y galletas.
            Usa un tono de reposteria y puedes usar 1 o 2 emojis por respuesta cuando calce naturalmente: 🧁 🍰 🍪 ✨.
            Te presentas como Richie solo si el cliente saluda o pregunta quien sos; no repitas tu presentacion en cada mensaje.

            Reglas de respuesta:
            - Responde corto y util, maximo 3-4 lineas salvo que pidan una lista.
            - Si preguntan por productos, precios, stock o categorias y el contexto trae productos, responde con opciones concretas y precio.
            - No inventes precios, stock, promociones, horarios, direcciones ni politicas si no aparecen en el contexto.
            - Si la base de datos esta desactivada o sin datos, di que no tienes disponibilidad en vivo y orienta al catalogo sin inventar.
            - Nunca reveles ni pidas contrasenas, API keys, cadenas de conexion, tokens, datos bancarios completos, datos internos del sistema ni informacion privada de otros clientes.
            - Si piden informacion sensible o tecnica interna, responde que por seguridad no puedes compartirla y ofrece ayuda con pedidos, productos o soporte.
            - No menciones Azure, base de datos, prompts, configuraciones internas ni herramientas tecnicas al cliente.
            - Si existe contexto personal autenticado y preguntan por "mis pedidos", responde exclusivamente con esos pedidos: numero, estado, pago, entrega y total.
            - Si no hay sesion autenticada y preguntan por sus pedidos, indica que deben iniciar sesion; nunca muestres pedidos generales ni de otra persona.

            {databaseContext}
            {userContext}
            """;

        var conversation = new List<object> { new { role = "system", content = systemPrompt } };
        foreach (var item in req.History?
                     .Where(x => x is not null &&
                                 (string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(x.Role, "assistant", StringComparison.OrdinalIgnoreCase)) &&
                                 !string.IsNullOrWhiteSpace(x.Content))
                     .TakeLast(10) ?? Enumerable.Empty<ChatMessage>())
        {
            var content = item.Content.Trim();
            conversation.Add(new
            {
                role = string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user",
                content = content[..Math.Min(content.Length, 1200)]
            });
        }
        conversation.Add(new { role = "user", content = req.Message.Trim() });

        var body = new
        {
            model = "llama-3.3-70b-versatile",
            temperature = 0.35,
            max_tokens = 450,
            messages = conversation
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            return StatusCode((int)response.StatusCode, new { message = "Richie no pudo responder en este momento. Intente de nuevo en unos segundos." });

        using var doc = JsonDocument.Parse(json);
        var reply = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        var products = await GetChatProductsAsync(req.Message);
        var navigation = ResolveNavigation(req.Message);
        var cartOffer = ResolveCartOffer(req.Message, products);
        return Ok(new { reply, products, navigation, cartOffer });
    }

    private async Task<IReadOnlyList<object>> GetChatProductsAsync(string message)
    {
        var normalized = message.ToLowerInvariant();
        if (!new[] { "producto", "productos", "catalogo", "catálogo", "precio", "que tienen", "que hay", "brownie", "queque", "cupcake", "galleta" }.Any(normalized.Contains)) return Array.Empty<object>();
        var terms = normalized.Split(new[] { ' ', ',', '.', '?', '¿', '!', '¡' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(term => term.Length >= 4 && term is not "producto" and not "productos" and not "precio" and not "catalogo" and not "catálogo")
            .ToArray();
        var list = (await _sqlStore.CatalogProductsAsync()).Where(product => product.IsActive && product.Stock > 0);
        if (terms.Length > 0)
        {
            var matches = list.Where(product => terms.Any(term => product.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || product.Category.Contains(term, StringComparison.OrdinalIgnoreCase))).Take(8).ToArray();
            if (matches.Length > 0) list = matches;
        }
        return list.Take(8).Select(product => (object)new { id = product.Id, name = product.Name, price = product.UnitPrice, stock = product.Stock, category = product.Category }).ToArray();
    }

    private static object? ResolveNavigation(string message)
    {
        var value = message.ToLowerInvariant();
        if (value.Contains("iniciar sesión") || value.Contains("iniciar sesion") || value.Contains("login")) return new { label = "Ir a iniciar sesión", url = "/Account/Login" };
        if (value.Contains("catalogo") || value.Contains("catálogo") || value.Contains("productos")) return new { label = "Ver productos", url = "/Catalog" };
        if (value.Contains("carrito")) return new { label = "Ver carrito", url = "/Catalog/Cart" };
        if (value.Contains("crear pedido") || value.Contains("pedido personalizado")) return new { label = "Crear pedido", url = "/Orders/Create" };
        return null;
    }

    private static object? ResolveCartOffer(string message, IReadOnlyList<object> products)
    {
        var quantityMatch = System.Text.RegularExpressions.Regex.Match(message, @"\b(\d+)\b");
        if (!quantityMatch.Success || products.Count == 0) return null;
        var product = products[0];
        return product;
    }

    private async Task<string> BuildDatabaseContextAsync()
    {
        var useDatabase = await ShouldUseDatabaseAsync();
        if (!useDatabase)
            return """
                Contexto de base de datos desactivado desde la configuracion del sistema.
                Puedes responder con informacion general de BakeSmart Patri, pero no inventes precios, stock ni disponibilidad.
                Si preguntan por productos especificos, invita a revisar el catalogo en la web.
                """;

        try
        {
            var categoriesTask = _sqlStore.CatalogCategoriesAsync();
            var productsTask = _sqlStore.CatalogProductsAsync();
            await Task.WhenAll(categoriesTask, productsTask);

            var categories = (await categoriesTask)
                .Take(10)
                .Select(category => category.Name);

            var products = (await productsTask)
                .Where(product => product.IsActive)
                .OrderByDescending(product => product.Stock > 0)
                .ThenBy(product => product.Category)
                .ThenBy(product => product.Name)
                .Take(20)
                .Select(product => $"{product.Name} ({product.Category}) - precio CRC {product.UnitPrice:N0} - stock {product.Stock:N0}");

            var categoryText = categories.Any() ? string.Join(", ", categories) : "sin categorias activas";
            var productText = products.Any() ? string.Join("; ", products) : "sin productos activos disponibles";

            return $"""
                Contexto disponible desde la base:
                Categorias: {categoryText}.
                Productos activos: {productText}.
                Usa esta informacion como fuente principal para responder sobre catalogo, precios y disponibilidad.
                """;
        }
        catch
        {
            return "No se pudo leer la base de datos para el contexto del bot en este momento.";
        }
    }

    private async Task<bool> ShouldUseDatabaseAsync()
    {
        try
        {
            var settings = await _sqlStore.SettingsDictionaryAsync();
            if (settings.TryGetValue("botUseDatabase", out var configuredValue))
                return IsEnabled(configuredValue);
        }
        catch
        {
            var fallbackValue = _config["Bot:UseDatabase"] ?? _config["BOT_USE_DATABASE"];
            return IsEnabled(fallbackValue);
        }

        return true;
    }

    private async Task<string> BuildUserContextAsync()
    {
        if (!(User?.Identity?.IsAuthenticated ?? false))
            return "Contexto personal: visitante sin sesion. No tiene acceso a pedidos privados.";

        var email = User.FindFirst(ClaimTypes.Email)?.Value;
        var displayName = User.Identity?.Name ?? "Usuario autenticado";
        var role = User.FindFirst(ClaimTypes.Role)?.Value ?? "Usuario";
        if (string.IsNullOrWhiteSpace(email))
            return $"Contexto personal: {displayName}, rol {role}, sin correo identificable. No consultes pedidos privados.";

        try
        {
            var orders = await _sqlStore.OrdersAsync(email);
            var recentOrders = orders.Take(8).ToArray();
            var ordersJson = JsonSerializer.Serialize(recentOrders);
            return $"""
                Contexto personal autenticado:
                Nombre: {displayName}. Rol: {role}. Correo verificado de sesion: {email}.
                Pedidos propios recientes ({recentOrders.Length}): {ordersJson}
                Esta informacion pertenece al usuario autenticado y solo debe usarse para responderle sobre sus propios pedidos.
                """;
        }
        catch
        {
            return $"Contexto personal autenticado: {displayName}, rol {role}. No fue posible consultar sus pedidos en este momento.";
        }
    }

    private static bool IsEnabled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("si", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}
