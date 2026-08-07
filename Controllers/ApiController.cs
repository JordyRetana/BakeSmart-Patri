using BakeSmartPatri.Data;
using BakeSmartPatri.Services;
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
    private readonly IWebHostEnvironment _environment;
    private readonly ReportExportService _reportExportService;
    private readonly IEmailService _emailService;

    public ApiController(SqlStore sqlStore, IHttpClientFactory httpClientFactory, IWebHostEnvironment environment, ReportExportService reportExportService, IEmailService emailService)
    {
        _sqlStore = sqlStore;
        _httpClientFactory = httpClientFactory;
        _environment = environment;
        _reportExportService = reportExportService;
        _emailService = emailService;
    }

    private string? CurrentUserEmail =>
        User?.FindFirst(ClaimTypes.Email)?.Value ??
        User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? null;

    [HttpGet("health")]
    public async Task<IActionResult> Health()
    {
        try
        {
            var health = await _sqlStore.HealthAsync();
            return Ok(health);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                enabled = true,
                status = "error",
                database = "offline",
                message = ex.Message
            });
        }
    }

    [HttpGet("dashboard")]
    [Authorize(Policy = "StaffOrAdmin")]
    public async Task<IActionResult> Dashboard() => Json(await _sqlStore.DashboardAsync());

    [HttpGet("orders")]
    [Authorize(Policy = "AnyUser")]
    public async Task<IActionResult> Orders()
    {
        if (User.IsInRole("Cliente"))
            return Json(await _sqlStore.OrdersAsync(CurrentUserEmail));

        return Json(await _sqlStore.OrdersAsync());
    }

    [HttpPost("orders/{id:int}/status")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> UpdateOrderStatus(int id, [FromBody] UpdateOrderStatusRequest request)
    {
        return BadRequest(new { message = "El estado operativo no se cambia manualmente. Use el flujo de Pedidos y Produccion." });
    }

    [HttpPost("orders/{id:int}/send-production")]
    [Authorize(Policy = "StaffOrAdmin")]
    public async Task<IActionResult> SendOrderToProduction(int id)
    {
        try
        {
            var status = await _sqlStore.SendOrderToProductionAsync(id, CurrentUserEmail);
            return Ok(new { ok = true, status });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("orders/{id:int}/advance-delivery")]
    [Authorize(Policy = "StaffOrAdmin")]
    public async Task<IActionResult> AdvanceOrderDelivery(int id)
    {
        try
        {
            var status = await _sqlStore.AdvanceOrderDeliveryAsync(id, CurrentUserEmail);
            return Ok(new { ok = true, status });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("production/{id:int}/advance")]
    [Authorize(Roles = "Admin,Staff,Repostero,Supervisor")]
    public async Task<IActionResult> AdvanceProduction(int id)
    {
        try
        {
            var status = await _sqlStore.AdvanceProductionOrderAsync(id, CurrentUserEmail);
            return Ok(new { ok = true, status });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("orders/{id:int}/pay")]
    [Authorize(Policy = "AnyUser")]
    public async Task<IActionResult> MarkOrderPaid(int id, [FromBody] MarkPaidRequest request)
    {
        if (User.IsInRole("Cliente"))
        {
            if (!await _sqlStore.OrderBelongsToAsync(id, CurrentUserEmail)) return Forbid();
        }
        await _sqlStore.MarkOrderPaidAsync(id, string.IsNullOrWhiteSpace(request.Method) ? "Efectivo" : request.Method, CurrentUserEmail);
        return Ok(new { ok = true });
    }

    [HttpDelete("orders/{id:int}")]
    [Authorize(Policy = "StaffOrAdmin")]
    public async Task<IActionResult> DeleteOrder(int id)
    {
        await _sqlStore.DeleteOrderAsync(id, CurrentUserEmail);
        return Ok(new { ok = true });
    }

    [HttpGet("inventory")]
    [Authorize(Policy = "StaffOrAdmin")]
    public async Task<IActionResult> Inventory() => Json(await _sqlStore.InventoryAsync());

    [HttpGet("inventory/catalog")]
    [Authorize(Policy = "AnyUser")]
    public async Task<IActionResult> CatalogInventory()
    {
        var products = await _sqlStore.CatalogProductsAsync();
        return Json(products.Where(product => product.IsActive).Select(product => new
        {
            id = product.Id,
            sku = product.Code,
            item = product.Name,
            description = product.Name,
            type = "Producto terminado",
            category = product.Category,
            subcategory = product.Subcategory,
            price = product.UnitPrice,
            stock = product.Stock,
            active = product.IsActive,
            imageUrl = product.ImageUrl
        }));
    }

    [HttpGet("inventory/categories")]
    [Authorize(Policy = "StaffOrAdmin")]
    public async Task<IActionResult> InventoryCategories() => Json(await _sqlStore.ProductCategoryOptionsAsync());

    [HttpPost("inventory")]
    [Authorize(Policy = "StaffOrAdmin")]
    public async Task<IActionResult> SaveInventoryProduct([FromBody] SqlStore.InventoryProductInput? request)
    {
        if (request is null)
            return BadRequest(new { message = "No se recibio la informacion del producto." });

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
        catch (Exception ex)
        {
            HttpContext.RequestServices.GetRequiredService<ILogger<ApiController>>()
                .LogError(ex, "No se pudo guardar el producto de inventario {Code}", request.Code);
            var environment = HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>();
            var message = environment.IsDevelopment()
                ? $"No se pudo guardar el producto: {ex.GetBaseException().Message}"
                : "No se pudo guardar el producto. Verifique el código y la categoría.";
            return StatusCode(500, new { message });
        }
    }

    [HttpPost("inventory/{id:int}/toggle")]
    [Authorize(Policy = "StaffOrAdmin")]
    public async Task<IActionResult> ToggleInventoryProduct(int id)
    {
        await _sqlStore.ToggleInventoryProductAsync(id, CurrentUserEmail);
        return Ok(new { ok = true });
    }

    [HttpGet("inventory/movements")]
    [Authorize(Policy = "StaffOrAdmin")]
    public async Task<IActionResult> InventoryMovements() => Json(await _sqlStore.InventoryMovementsAsync());

    [HttpPost("inventory/movements")]
    [Authorize(Policy = "StaffOrAdmin")]
    public async Task<IActionResult> RegisterInventoryMovement([FromBody] SqlStore.InventoryMovementInput request)
    {
        if (request.ProductId <= 0 || request.Quantity <= 0)
            return BadRequest(new { message = "Debe indicar producto y cantidad valida." });

        await _sqlStore.RegisterInventoryMovementAsync(request, CurrentUserEmail);
        return Ok(new { ok = true });
    }

    [HttpGet("customers")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Customers() => Json(await _sqlStore.CustomersAsync());

    [HttpGet("profile/current")]
    [Authorize(Policy = "AnyUser")]
    public async Task<IActionResult> CurrentProfile()
    {
        var email = CurrentUserEmail;
        if (string.IsNullOrWhiteSpace(email))
            return Unauthorized(new { message = "Debe iniciar sesion." });

        var profile = await _sqlStore.GetProfileAsync(email);
        return profile is null
            ? NotFound(new { message = "No se encontro el perfil." })
            : Json(profile);
    }

    [HttpGet("promotions")]
    public async Task<IActionResult> Promotions() => Json(await _sqlStore.PromotionsAsync());

    [HttpGet("catalog/options")]
    [AllowAnonymous]
    public async Task<IActionResult> CatalogOptions()
    {
        var categoriesTask = _sqlStore.CatalogCategoriesAsync();
        var productsTask = _sqlStore.CatalogProductsAsync();
        await Task.WhenAll(categoriesTask, productsTask);

        var products = await productsTask;
        var imageRoot = Path.Combine(_environment.WebRootPath, "img");
        var staticImages = Directory.Exists(imageRoot)
            ? Directory.EnumerateFiles(imageRoot, "*.*", SearchOption.AllDirectories)
                .Where(path => IsAllowedImageExtension(Path.GetExtension(path)))
                .Select(path => "/" + Path.GetRelativePath(_environment.WebRootPath, path).Replace("\\", "/"))
            : Enumerable.Empty<string>();

        var imageOptions = products
            .Select(product => product.ImageUrl)
            .Concat(staticImages)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(url => url)
            .ToList();

        return Json(new
        {
            categories = (await categoriesTask).Select(category => new
            {
                category.Id,
                category.Name,
                category.Icon,
                url = $"/Catalog?category={Uri.EscapeDataString(category.Name)}"
            }),
            products = products.Where(product => product.IsActive).Select(product => new
            {
                product.Id,
                product.Name,
                product.Category,
                product.ImageUrl,
                url = $"/Catalog/Details/{product.Id}"
            }),
            images = imageOptions
        });
    }

    [HttpPost("assets/site-images")]
    [Authorize(Policy = "StaffOrAdmin")]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> UploadSiteImage(IFormFile? file)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Seleccione una imagen para subir." });

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!IsAllowedImageExtension(extension))
            return BadRequest(new { message = "Formato no permitido. Use JPG, PNG, WEBP o GIF." });

        if (file.Length > 8 * 1024 * 1024)
            return BadRequest(new { message = "La imagen no puede superar 8 MB." });

        var uploadFolder = Path.Combine(_environment.WebRootPath, "img", "uploads", "site");
        Directory.CreateDirectory(uploadFolder);

        var fileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(uploadFolder, fileName);
        await using (var stream = System.IO.File.Create(fullPath))
        {
            await file.CopyToAsync(stream);
        }

        var url = $"/img/uploads/site/{fileName}";
        await _sqlStore.AddAuditLogAsync("SUBIR_IMAGEN_SITIO", $"Imagen del sitio cargada: {url}", CurrentUserEmail);
        return Ok(new { ok = true, url });
    }

    [HttpPost("promotions")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> SavePromotion([FromBody] SqlStore.PromotionInput request)
    {
        try
        {
            var id = await _sqlStore.SavePromotionAsync(request, CurrentUserEmail);
            return Ok(new { ok = true, id });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("promotions/{id:int}/toggle")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> TogglePromotion(int id)
    {
        try
        {
            await _sqlStore.TogglePromotionAsync(id, CurrentUserEmail);
            return Ok(new { ok = true });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("combos")]
    [AllowAnonymous]
    public async Task<IActionResult> Combos([FromQuery] bool activeOnly = false) => Json(await _sqlStore.CombosAsync(activeOnly));

    [HttpPost("combos")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> SaveCombo([FromBody] SqlStore.ComboInput? request)
    {
        if (request is null) return BadRequest(new { message = "No se recibió la información del combo." });
        try { return Ok(new { ok = true, id = await _sqlStore.SaveComboAsync(request, CurrentUserEmail) }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("combos/{id:int}/toggle")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> ToggleCombo(int id)
    {
        await _sqlStore.ToggleComboAsync(id, CurrentUserEmail);
        return Ok(new { ok = true });
    }

    [HttpDelete("combos/{id:int}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> DeleteCombo(int id)
    {
        try { await _sqlStore.DeleteComboAsync(id, CurrentUserEmail); return Ok(new { ok = true }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("customers/{id:int}/frequent")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> ToggleFrequentCustomer(int id)
    {
        var isFrequent = await _sqlStore.MarkCustomerFrequentAsync(id, CurrentUserEmail);
        var emailSent = false;
        var recipient = (await _sqlStore.MarketingRecipientsAsync(new[] { id })).FirstOrDefault();
        if (recipient is not null)
        {
            var subject = isFrequent
                ? "Bienvenido a clientes frecuentes de Repostería Patri"
                : "Actualización de cliente frecuente de Repostería Patri";
            var message = isFrequent
                ? $"Hola {recipient.FullName}, ahora formas parte de nuestros clientes frecuentes. Recibirás promociones y beneficios especiales de Repostería Patri."
                : $"Hola {recipient.FullName}, te confirmamos que tu estado de cliente frecuente fue desactivado. Puedes volver a formar parte del programa cuando se active nuevamente.";

            await _emailService.SendAsync(recipient.Email, recipient.FullName, subject, message);
            emailSent = true;
        }

        return Ok(new { ok = true, frequent = isFrequent, emailSent });
    }

    [HttpPost("marketing/campaigns")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> SendMarketingCampaign([FromBody] SqlStore.MarketingCampaignInput request)
    {
        try
        {
            var recipients = await _sqlStore.MarketingRecipientsAsync(request.CustomerIds ?? Array.Empty<int>());
            if (recipients.Count == 0)
                return BadRequest(new { message = "No hay clientes registrados con un correo válido." });
            if (recipients.Count > 50)
                return BadRequest(new { message = "Puede enviar una campaña a un máximo de 50 clientes por operación." });

            foreach (var recipient in recipients)
            {
                await _emailService.SendAsync(
                    recipient.Email,
                    recipient.FullName,
                    string.IsNullOrWhiteSpace(request.Subject) ? "Promoción Repostería Patri" : request.Subject,
                    request.Message);
            }

            var campaign = request with { CustomerIds = recipients.Select(recipient => recipient.CustomerId).ToArray() };
            var id = await _sqlStore.SendMarketingCampaignAsync(campaign, CurrentUserEmail);
            return Ok(new { ok = true, id, sent = recipients.Count });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("public/contact")]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> SendContact([FromBody] ContactMessageRequest? request, [FromServices] IConfiguration configuration)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { message = "Complete su nombre, correo y mensaje." });
        if (!IsValidEmail(request.Email))
            return BadRequest(new { message = "Ingrese un correo electrónico válido." });

        var recipient = configuration["Brevo:ContactRecipient"];
        if (string.IsNullOrWhiteSpace(recipient))
            return StatusCode(503, new { message = "El correo de contacto todavía no está configurado." });

        var subject = string.IsNullOrWhiteSpace(request.Subject) ? "Consulta desde el sitio web" : request.Subject.Trim();
        var body = $"Nombre: {request.Name.Trim()}\nCorreo: {request.Email.Trim()}\nTeléfono: {request.Phone?.Trim() ?? "No indicado"}\nTipo: {subject}\n\nMensaje:\n{request.Message.Trim()}";
        try
        {
            await _emailService.SendAsync(recipient, "Repostería Patri", $"Nueva consulta: {subject}", body);
            await _emailService.SendAsync(request.Email, request.Name, "Recibimos su consulta", "Gracias por escribirnos. Recibimos su consulta y nuestro equipo le responderá lo antes posible.");
            return Ok(new { ok = true });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { message = ex.Message });
        }
    }

    [HttpPost("public/newsletter")]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> SubscribeNewsletter([FromBody] NewsletterRequest? request)
    {
        var email = request?.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IsValidEmail(email))
            return BadRequest(new { message = "Ingrese un correo electrónico válido." });

        try
        {
            await _emailService.SendAsync(email, email, "Suscripción confirmada", "Ya forma parte de nuestras novedades. Le enviaremos promociones, productos y fechas especiales de Repostería Patri.");
            await _sqlStore.SubscribeNewsletterAsync(email);
            return Ok(new { ok = true });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { message = ex.Message });
        }
    }

    [HttpGet("users")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Users() => Json(await _sqlStore.UsersAsync());

    [HttpPost("users")]
    [Authorize(Policy = "AdminOnly")]
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
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> ToggleUser(int id)
    {
        await _sqlStore.ToggleUserAsync(id);
        await _sqlStore.AddAuditLogAsync("USUARIO_TOGGLE", $"Usuario ID {id} cambio de estado", CurrentUserEmail);
        return Ok(new { ok = true });
    }



    [HttpGet("roles")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Roles() => Json(await _sqlStore.RolesAsync());

    [HttpGet("pos/config")]
    public async Task<IActionResult> PosConfig() => Json(await _sqlStore.PosConfigAsync());

    [HttpPost("pos/payment-methods")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> SavePaymentMethod([FromBody] SqlStore.PaymentMethodInput request)
    {
        try
        {
            var id = await _sqlStore.SavePaymentMethodAsync(request, CurrentUserEmail);
            return Ok(new { ok = true, id });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("pos/payment-methods/{id:int}/toggle")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> TogglePaymentMethod(int id)
    {
        await _sqlStore.TogglePaymentMethodAsync(id, CurrentUserEmail);
        return Ok(new { ok = true });
    }

    [HttpGet("logs")]
    [Authorize(Roles = "Admin,Staff,Supervisor")]
    public async Task<IActionResult> Logs() => Json(await _sqlStore.AuditLogsAsync());

    [HttpPost("orders")]
    [Authorize(Policy = "AnyUser")]
    public async Task<IActionResult> CreateOrder([FromBody] SqlStore.CreateOrderInput? request)
    {
        if (request is null)
            return BadRequest(new { message = "El pedido enviado no tiene un formato valido." });

        if (User.IsInRole("Cliente"))
        {
            var profile = await _sqlStore.GetProfileAsync(CurrentUserEmail ?? string.Empty);
            if (profile is null) return Unauthorized(new { message = "No se encontro el perfil autenticado." });
            request = request with
            {
                CustomerName = $"{profile.FirstName} {profile.LastName}".Trim(),
                Email = profile.Email,
                Phone = profile.Phone
            };
        }
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
        try
        {
            await _sqlStore.CloseCashSessionAsync(request.Id, request.DeclaredAmount, CurrentUserEmail);
            return Ok(new { ok = true });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("pos/sessions")]
    [Authorize(Policy = "StaffOrAdmin")]
    public async Task<IActionResult> CashSessions()
    {
        var canSeeAll = User.IsInRole("Admin") || User.IsInRole("Staff") || User.IsInRole("Supervisor");
        return Json(await _sqlStore.CashSessionsAsync(CurrentUserEmail, canSeeAll));
    }

    [HttpGet("pos/sales")]
    [Authorize(Roles = "Admin,Staff,Supervisor")]
    public async Task<IActionResult> RecentPosSales() => Json(await _sqlStore.RecentPosSalesAsync());

    [AllowAnonymous]
    [HttpPost("auth/forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest(new { message = "Indique su correo electronico." });

        var email = request.Email.Trim().ToLowerInvariant();
        var token = await _sqlStore.CreatePasswordResetTokenAsync(email);
        if (token is not null)
        {
            var resetUrl = Url.Action("ResetPassword", "Account", new { token }, Request.Scheme, Request.Host.Value)!;
            try
            {
                await _emailService.SendAsync(email, email, "Restablecer contraseña", $"Abra este enlace durante los próximos 30 minutos:\n\n{resetUrl}\n\nSi no realizó esta solicitud, ignore este correo.");
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(503, new { message = ex.Message });
            }
        }
        return Ok(new { ok = true, message = "Si el correo está registrado, recibirá instrucciones para restablecer su contraseña." });
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
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("pos/credit-notes")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> RegisterCreditNote([FromBody] SqlStore.CreditNoteInput request)
    {
        try
        {
            var id = await _sqlStore.RegisterCreditNoteAsync(request, CurrentUserEmail);
            return Ok(new { ok = true, id });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("accounting")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Accounting() => Json(await _sqlStore.AccountingOverviewAsync());

    [HttpPost("accounting/expenses")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> RegisterExpense([FromBody] SqlStore.AccountingExpenseInput request)
    {
        try
        {
            var id = await _sqlStore.RegisterExpenseAsync(request, CurrentUserEmail);
            return Ok(new { ok = true, id });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("accounting/supplier-payments")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> RegisterSupplierPayment([FromBody] SqlStore.SupplierPaymentInput request)
    {
        try
        {
            var id = await _sqlStore.RegisterSupplierPaymentAsync(request, CurrentUserEmail);
            return Ok(new { ok = true, id });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("accounting/reconcile-pos")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> ReconcilePos()
    {
        try
        {
            var result = await _sqlStore.ReconcilePosAsync(CurrentUserEmail);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("accounting/daily-close")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> DailyAccountingClose([FromBody] AccountingCloseRequest? request = null)
    {
        try
        {
            var type = string.IsNullOrWhiteSpace(request?.Type) ? "DIARIO" : request.Type;
            var result = await _sqlStore.AccountingCloseAsync(type, CurrentUserEmail);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("settings")]
    [Authorize(Policy = "StaffOrAdmin")]
    public async Task<IActionResult> Settings() => Json(await _sqlStore.GetSettingsAsync());

    [HttpPost("settings")]
    [Authorize(Policy = "StaffOrAdmin")]
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
    [Authorize(Roles = "Admin,Supervisor")]
    public async Task<IActionResult> Reports(string type, DateTime? start, DateTime? end)
    {
        return Json(await _sqlStore.ReportsAsync(type, start, end));
    }

    [HttpGet("reports/{type}/export/{format}")]
    [Authorize(Roles = "Admin,Supervisor")]
    public async Task<IActionResult> ExportReport(string type, string format, DateTime? start, DateTime? end)
    {
        var report = await _sqlStore.ReportsAsync(type, start, end);
        var safeType = type switch
        {
            "sales" => "ventas",
            "inventory" => "inventario",
            "users" => "usuarios",
            "promotions" => "promociones",
            "cashClosures" => "cierres_de_caja",
            "orders" => "pedidos",
            _ => "reporte"
        };
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        return format.ToLowerInvariant() switch
        {
            "xlsx" => File(_reportExportService.CreateExcel(type, report, start, end), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"reporte_{safeType}_{stamp}.xlsx"),
            "pdf" => File(_reportExportService.CreatePdf(type, report, start, end), "application/pdf", $"reporte_{safeType}_{stamp}.pdf"),
            _ => BadRequest(new { message = "Formato de reporte no soportado." })
        };
    }

    public sealed record UpdateOrderStatusRequest(string Status);
    public sealed record MarkPaidRequest(string Method);
    public sealed record OpenCashSessionRequest(decimal Amount);
    public sealed record CloseCashSessionRequest(int Id, decimal DeclaredAmount);
    public sealed record AccountingCloseRequest(string? Type);
    public sealed record ForgotPasswordRequest(string Email);
    public sealed record ContactMessageRequest(string Name, string Email, string? Phone, string? Subject, string Message);
    public sealed record NewsletterRequest(string? Email);

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

    private static bool IsAllowedImageExtension(string extension)
    {
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }
}
