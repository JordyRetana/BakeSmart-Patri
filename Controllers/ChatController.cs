using BakeSmartPatri.Data;
using Microsoft.AspNetCore.Mvc;
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

    public sealed record ChatRequest(string Message);

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ChatRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Message))
            return BadRequest(new { message = "Escriba un mensaje para el asistente." });

        var apiKey = _config["Groq:ApiKey"] ?? _config["GROQ_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return BadRequest(new { message = "Falta configurar la API key del bot." });

        var databaseContext = await BuildDatabaseContextAsync();
        var systemPrompt = $"""
            Sos Richie, el asistente virtual de BakeSmart Patri, una pasteleria/reposteria en Costa Rica.
            Hablas en tono cercano, calido y con un toque tico, pero profesional.
            Te presentas como Richie la primera vez que alguien te escribe.
            No inventes precios ni productos especificos si no te los dan en el contexto.
            Si el contexto incluye categorias o productos, debes usarlos en tu respuesta.
            Si preguntan por productos, precios, stock o categorias y hay productos en el contexto, lista opciones concretas con precio.
            Solo envia al usuario a revisar el catalogo si el contexto de base de datos esta desactivado o no contiene datos suficientes.
            Manten las respuestas cortas, maximo 3-4 lineas.

            {databaseContext}
            """;

        var body = new
        {
            model = "llama-3.3-70b-versatile",
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = req.Message }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            return StatusCode((int)response.StatusCode, new { message = "El servicio del bot no respondio correctamente." });

        using var doc = JsonDocument.Parse(json);
        var reply = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return Ok(new { reply });
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
