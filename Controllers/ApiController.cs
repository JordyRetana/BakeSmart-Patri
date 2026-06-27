using BakeSmartPatri.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;

namespace BakeSmartPatri.Controllers;

[Route("api")]
public class ApiController : Controller
{
    private readonly SqlStore _sqlStore;
    private readonly IHttpClientFactory _httpClientFactory;

    public ApiController(SqlStore sqlStore, IHttpClientFactory httpClientFactory)
    {
        _sqlStore = sqlStore;
        _httpClientFactory = httpClientFactory;
    }

    private string? CurrentUserEmail =>
        User?.FindFirst(ClaimTypes.Email)?.Value ??
        User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? null;

    [HttpGet("health")]
    public async Task<IActionResult> Health()
    {
        try
        {
            return Json(await _sqlStore.HealthAsync());
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                enabled = true,
                status = "error",
                message = ex.Message
            });
        }
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard() => Json(await _sqlStore.DashboardAsync());

    [HttpGet("orders")]
    public async Task<IActionResult> Orders() => Json(await _sqlStore.OrdersAsync());

    [HttpPost("orders/{id:int}/status")]
    public async Task<IActionResult> UpdateOrderStatus(int id, [FromBody] UpdateOrderStatusRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Status))
            return BadRequest(new { message = "Debe indicar el estado." });

        await _sqlStore.UpdateOrderStatusAsync(id, request.Status);
        return Ok(new { ok = true });
    }

    [HttpPost("orders/{id:int}/pay")]
    public async Task<IActionResult> MarkOrderPaid(int id, [FromBody] MarkPaidRequest request)
    {
        await _sqlStore.MarkOrderPaidAsync(id, string.IsNullOrWhiteSpace(request.Method) ? "Efectivo" : request.Method);
        return Ok(new { ok = true });
    }

    [HttpGet("inventory")]
    public async Task<IActionResult> Inventory() => Json(await _sqlStore.InventoryAsync());

    [HttpPost("inventory")]
    public async Task<IActionResult> SaveInventoryProduct([FromBody] SqlStore.InventoryProductInput request)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Description))
            return BadRequest(new { message = "Debe indicar codigo y descripcion." });

        if (request.Stock < 0 || request.MinStock < 0 || request.Price < 0)
            return BadRequest(new { message = "Los valores numericos no pueden ser negativos." });

        try
        {
            var productId = await _sqlStore.SaveInventoryProductAsync(request, CurrentUserEmail);
            return Ok(new { ok = true, id = productId });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("inventory/{id:int}/toggle")]
    public async Task<IActionResult> ToggleInventoryProduct(int id)
    {
        await _sqlStore.ToggleInventoryProductAsync(id, CurrentUserEmail);
        return Ok(new { ok = true });
    }

    [HttpGet("inventory/movements")]
    public async Task<IActionResult> InventoryMovements() => Json(await _sqlStore.InventoryMovementsAsync());

    [HttpPost("inventory/movements")]
    public async Task<IActionResult> RegisterInventoryMovement([FromBody] SqlStore.InventoryMovementInput request)
    {
        if (request.ProductId <= 0 || request.Quantity <= 0)
            return BadRequest(new { message = "Debe indicar producto y cantidad valida." });

        await _sqlStore.RegisterInventoryMovementAsync(request, CurrentUserEmail);
        return Ok(new { ok = true });
    }

    [HttpGet("customers")]
    public async Task<IActionResult> Customers() => Json(await _sqlStore.CustomersAsync());

    [HttpGet("promotions")]
    public async Task<IActionResult> Promotions() => Json(await _sqlStore.PromotionsAsync());

    [HttpGet("users")]
    public async Task<IActionResult> Users() => Json(await _sqlStore.UsersAsync());

    [HttpPost("users")]
    public async Task<IActionResult> SaveUser([FromBody] SqlStore.UserInput request)
    {
        request = request with
        {
            FirstName = (request.FirstName ?? "").Trim(),
            LastName = (request.LastName ?? "").Trim(),
            Email = (request.Email ?? "").Trim().ToLowerInvariant(),
            Phone = (request.Phone ?? "").Trim(),
            Address = (request.Address ?? "").Trim(),
            Role = (request.Role ?? "").Trim(),
            Password = (request.Password ?? "").Trim()
        };

        if (string.IsNullOrWhiteSpace(request.FirstName) ||
            string.IsNullOrWhiteSpace(request.LastName) ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Phone) ||
            string.IsNullOrWhiteSpace(request.Role))
            return BadRequest(new { message = "Complete nombre, apellidos, correo, telefono y rol." });

        if (!IsValidEmail(request.Email))
            return BadRequest(new { message = "Ingrese un correo valido." });

        if (request.Id is null && string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { message = "Debe indicar una contraseña para el usuario nuevo." });

        if (!string.IsNullOrWhiteSpace(request.Password) && request.Password.Length < 8)
            return BadRequest(new { message = "La contraseña debe tener al menos 8 caracteres." });

        var userId = await _sqlStore.SaveUserAsync(request);

        var action = request.Id is > 0 ? "actualizado" : "creado";
        await _sqlStore.AddAuditLogAsync($"USUARIO_{action.ToUpperInvariant()}", $"Usuario '{request.Email}' {action}", CurrentUserEmail);

        return Ok(new { ok = true, id = userId });
    }

    [HttpPost("users/{id:int}/toggle")]
    public async Task<IActionResult> ToggleUser(int id)
    {
        await _sqlStore.ToggleUserAsync(id);
        await _sqlStore.AddAuditLogAsync("USUARIO_TOGGLE", $"Usuario ID {id} cambio de estado", CurrentUserEmail);
        return Ok(new { ok = true });
    }



    [HttpGet("roles")]
    public async Task<IActionResult> Roles() => Json(await _sqlStore.RolesAsync());

    [HttpGet("pos/config")]
    public async Task<IActionResult> PosConfig() => Json(await _sqlStore.PosConfigAsync());

    [HttpGet("logs")]
    [Authorize(Roles = "Admin,Staff,Supervisor")]
    public async Task<IActionResult> Logs() => Json(await _sqlStore.AuditLogsAsync());

    [HttpPost("orders")]
    [Authorize(Policy = "AnyUser")]
    public async Task<IActionResult> CreateOrder([FromBody] SqlStore.CreateOrderInput request)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerName) ||
            string.IsNullOrWhiteSpace(request.Email) ||
            request.ProductId <= 0 ||
            request.Quantity <= 0)
            return BadRequest(new { message = "Complete los datos obligatorios del pedido." });

        var deliveryMethod = (request.DeliveryMethod ?? "domicilio").Trim().ToLowerInvariant();
        if (deliveryMethod != "retiro")
        {
            if (string.IsNullOrWhiteSpace(request.Address))
                return BadRequest(new { message = "Debe indicar la direccion de entrega." });

            if (!SqlStore.HasValidCoordinates(request.DestinationLatitude, request.DestinationLongitude))
                return BadRequest(new { message = "Debe seleccionar una ubicacion valida en el mapa." });
        }

        try
        {
            var orderId = await _sqlStore.CreateOrderAsync(request, CurrentUserEmail);
            return Ok(new { ok = true, id = orderId });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("addresses/default")]
    [Authorize(Policy = "AnyUser")]
    public async Task<IActionResult> DefaultAddress()
    {
        var email = CurrentUserEmail;
        if (string.IsNullOrWhiteSpace(email))
            return Unauthorized(new { message = "Debe iniciar sesion." });

        var address = await _sqlStore.GetDefaultAddressByEmailAsync(email);
        return Json(address);
    }

    [HttpGet("addresses")]
    [Authorize(Policy = "AnyUser")]
    public async Task<IActionResult> Addresses()
    {
        var email = CurrentUserEmail;
        if (string.IsNullOrWhiteSpace(email))
            return Unauthorized(new { message = "Debe iniciar sesion." });

        return Json(await _sqlStore.GetAddressesByEmailAsync(email));
    }

    [AllowAnonymous]
    [HttpGet("geo/search")]
    public async Task<IActionResult> GeoSearch([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 3)
            return Json(Array.Empty<object>());

        var client = _httpClientFactory.CreateClient("Nominatim");
        var response = await client.GetAsync($"search?format=json&addressdetails=0&limit=6&q={Uri.EscapeDataString(q.Trim())}");
        if (!response.IsSuccessStatusCode)
            return StatusCode(502, new { message = "Servicio de geocodificacion no disponible." });

        using var stream = await response.Content.ReadAsStreamAsync();
        var results = await JsonSerializer.DeserializeAsync<JsonElement[]>(stream) ?? Array.Empty<JsonElement>();

        var payload = results.Select(item => new
        {
            displayName = item.GetProperty("display_name").GetString(),
            lat = item.GetProperty("lat").GetString(),
            lng = item.GetProperty("lon").GetString()
        });

        return Json(payload);
    }

    [AllowAnonymous]
    [HttpGet("geo/reverse")]
    public async Task<IActionResult> GeoReverse([FromQuery] decimal lat, [FromQuery] decimal lng)
    {
        if (!SqlStore.HasValidCoordinates(lat, lng))
            return BadRequest(new { message = "Coordenadas invalidas." });

        var client = _httpClientFactory.CreateClient("Nominatim");
        var response = await client.GetAsync($"reverse?format=json&lat={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&lon={lng.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (!response.IsSuccessStatusCode)
            return StatusCode(502, new { message = "Servicio de geocodificacion no disponible." });

        using var stream = await response.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync<JsonElement>(stream);
        var displayName = result.TryGetProperty("display_name", out var nameElement)
            ? nameElement.GetString()
            : $"{lat}, {lng}";

        return Json(new { displayName });
    }

    [HttpPost("pos/open")]
    [Authorize(Policy = "StaffOrAdmin")]
    public async Task<IActionResult> OpenCashSession([FromBody] OpenCashSessionRequest request)
    {
        try
        {
            var sessionId = await _sqlStore.OpenCashSessionAsync(request.Amount, CurrentUserEmail);
            return Ok(new { ok = true, id = sessionId });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("pos/close")]
    [Authorize(Policy = "StaffOrAdmin")]
    public async Task<IActionResult> CloseCashSession([FromBody] CloseCashSessionRequest request)
    {
        await _sqlStore.CloseCashSessionAsync(request.Id, request.DeclaredAmount, CurrentUserEmail);
        return Ok(new { ok = true });
    }

    [HttpGet("pos/sessions")]
    [Authorize(Policy = "StaffOrAdmin")]
    public async Task<IActionResult> CashSessions() => Json(await _sqlStore.CashSessionsAsync());

    [AllowAnonymous]
    [HttpPost("auth/forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest(new { message = "Indique su correo electronico." });

        var result = await _sqlStore.RequestPasswordResetAsync(request.Email.Trim().ToLowerInvariant());
        if (!result)
        {
            // No revelar si el correo existe o no
            return Ok(new { ok = true, message = "Si el correo esta registrado, recibira instrucciones para restablecer su contrasena." });
        }

        return Ok(new { ok = true, message = "Contrasena restablecida. Revise la bitacora del sistema para obtener la contrasena temporal (modo desarrollo)." });
    }

    [HttpPost("pos/sell")]
    [Authorize(Policy = "StaffOrAdmin")]
    public async Task<IActionResult> RegisterSale([FromBody] SqlStore.SaleInput request)
    {
        try
        {
            if (request.Items is null || request.Items.Count == 0)
                return BadRequest(new { message = "Debe incluir al menos un producto." });

            var orderId = await _sqlStore.RegisterSaleAsync(request, CurrentUserEmail);
            return Ok(new { ok = true, id = orderId });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("settings")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Settings() => Json(await _sqlStore.GetSettingsAsync());

    [HttpPost("settings")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> SaveSettings([FromBody] Dictionary<string, string> settings)
    {
        try
        {
            await _sqlStore.SaveSettingsAsync(settings);
            await _sqlStore.AddAuditLogAsync("CONFIGURACION", "Configuracion del sitio actualizada", CurrentUserEmail);
            return Ok(new { ok = true });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("reports/{type}")]
    public async Task<IActionResult> Reports(string type, DateTime? start, DateTime? end)
    {
        return Json(await _sqlStore.ReportsAsync(type, start, end));
    }

    public sealed record UpdateOrderStatusRequest(string Status);
    public sealed record MarkPaidRequest(string Method);
    public sealed record OpenCashSessionRequest(decimal Amount);
    public sealed record CloseCashSessionRequest(int Id, decimal DeclaredAmount);
    public sealed record ForgotPasswordRequest(string Email);

    private static bool IsValidEmail(string email)
    {
        try
        {
            var address = new MailAddress(email);
            return address.Address.Equals(email, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
