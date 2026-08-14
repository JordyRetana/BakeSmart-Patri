using BakeSmartPatri.Data;
using BakeSmartPatri.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Net.Mail;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
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
    private readonly IConfiguration _configuration;
    private readonly ILogger<ApiController> _logger;

    public ApiController(SqlStore sqlStore, IHttpClientFactory httpClientFactory, IWebHostEnvironment environment, ReportExportService reportExportService, IEmailService emailService, IConfiguration configuration, ILogger<ApiController> logger)
    {
        _sqlStore = sqlStore;
        _httpClientFactory = httpClientFactory;
        _environment = environment;
        _reportExportService = reportExportService;
        _emailService = emailService;
        _configuration = configuration;
        _logger = logger;
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
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "No se pudo enviar el pedido a Produccion.",
                detail = ex.GetBaseException().Message
            });
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
        var method = string.IsNullOrWhiteSpace(request.Method) ? "Efectivo" : request.Method.Trim();
        if (string.Equals(method, "PayPal", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "PayPal se confirma automáticamente al recibir la captura aprobada." });

        if (!string.Equals(method, "Efectivo", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(method, "SINPE", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "La forma de pago seleccionada no está disponible." });

        await _sqlStore.MarkOrderPaidAsync(id, method, CurrentUserEmail);
        return Ok(new { ok = true });
    }

    [HttpDelete("orders/{id:int}")]
    [Authorize(Policy = "AnyUser")]
    public async Task<IActionResult> DeleteOrder(int id)
    {
        if (User.IsInRole("Cliente") && !await _sqlStore.OrderBelongsToAsync(id, CurrentUserEmail))
            return Forbid();
        try
        {
            await _sqlStore.DeleteOrderAsync(id, CurrentUserEmail);
            return Ok(new { ok = true });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "No se pudo eliminar el pedido. Inténtelo de nuevo." });
        }
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

        var imageCandidates = products
            .Select(product => product.ImageUrl)
            .Concat(staticImages)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(url => url);

        // Distinct URLs can still point to byte-for-byte duplicate uploads. Keep one
        // visual copy so the settings gallery remains useful and compact.
        var imageOptions = new List<string>();
        var imageSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in imageCandidates)
        {
            var signature = url;
            try
            {
                var relativeUrl = url.Split('?', '#')[0].TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var webRoot = Path.GetFullPath(_environment.WebRootPath);
                var localPath = Path.GetFullPath(Path.Combine(webRoot, relativeUrl));
                if (localPath.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(localPath))
                {
                    using var stream = System.IO.File.OpenRead(localPath);
                    signature = Convert.ToHexString(SHA256.HashData(stream));
                }
            }
            catch
            {
                // A remote or temporarily unavailable image is still a valid option;
                // its normalized URL becomes the fallback identity.
            }

            if (imageSignatures.Add(signature))
                imageOptions.Add(url);
        }

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

            var paymentMethod = request.PaymentMethod?.Trim() ?? string.Empty;
            if (!string.Equals(paymentMethod, "Efectivo", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(paymentMethod, "SINPE", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "En Punto de Venta solo se permiten efectivo o SINPE Móvil. El datáfono estará disponible próximamente." });

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

    [HttpPost("payments/stripe/checkout")]
    [Authorize(Policy = "AnyUser")]
    public async Task<IActionResult> StartStripeCheckout([FromBody] StripeCheckoutRequest? request)
    {
        if (!_configuration.GetValue<bool>("Payments:StripeEnabled"))
            return StatusCode(StatusCodes.Status410Gone, new { message = "El pago con Stripe no está disponible. Seleccione PayPal u otro método habilitado." });

        var secret = _configuration["Stripe:SecretKey"];
        if (string.IsNullOrWhiteSpace(secret))
            return BadRequest(new { message = "Stripe todavía no está configurado en el servidor." });

        var ids = (request?.OrderIds ?? []).Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return BadRequest(new { message = "No hay pedidos válidos para cobrar." });

        var email = CurrentUserEmail ?? string.Empty;
        var rawOrders = JsonSerializer.SerializeToElement(await _sqlStore.OrdersAsync(User.IsInRole("Cliente") ? email : null));
        var selected = rawOrders.EnumerateArray()
            .Where(order => order.TryGetProperty("id", out var id) && ids.Contains(id.GetInt32()))
            .Where(order => !order.TryGetProperty("paymentStatus", out var status) || !string.Equals(status.GetString(), "Pagado", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (selected.Length != ids.Length) return BadRequest(new { message = "Uno de los pedidos no existe, no pertenece al usuario o ya fue pagado." });

        var totalToCharge = selected.Sum(order => order.GetProperty("total").GetDecimal());
        if (totalToCharge < 300m)
            return BadRequest(new { message = "Stripe no procesa cobros tan bajos. Para una prueba use un pedido de al menos ₡300." });

        var origin = $"{Request.Scheme}://{Request.Host}";
        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "payment"),
            new("success_url", $"{origin}/api/payments/stripe/complete?session_id={{CHECKOUT_SESSION_ID}}"),
            new("cancel_url", $"{origin}/Client/Orders?stripe=cancelled"),
            new("client_reference_id", string.Join(',', ids)),
            new("metadata[orders]", string.Join(',', ids)),
            new("metadata[email]", email)
        };
        for (var index = 0; index < selected.Length; index++)
        {
            var order = selected[index];
            var id = order.GetProperty("id").GetInt32();
            var total = order.GetProperty("total").GetDecimal();
            form.Add(new($"line_items[{index}][price_data][currency]", "crc"));
            form.Add(new($"line_items[{index}][price_data][product_data][name]", $"Pedido BakeSmart #{id}"));
            form.Add(new($"line_items[{index}][price_data][unit_amount]", decimal.Round(total * 100m, 0).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            form.Add(new($"line_items[{index}][quantity]", "1"));
        }

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{secret}:")));
        var response = await client.PostAsync("https://api.stripe.com/v1/checkout/sessions", new FormUrlEncodedContent(form));
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var detail = GetStripeErrorMessage(json);
            return BadRequest(new { message = string.IsNullOrWhiteSpace(detail) ? "Stripe no pudo iniciar el cobro." : $"Stripe rechazó el cobro: {detail}" });
        }
        using var document = JsonDocument.Parse(json);
        return Ok(new { url = document.RootElement.GetProperty("url").GetString() });
    }

    private static string? GetStripeErrorMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("error", out var error)) return null;
            var message = error.TryGetProperty("message", out var value) ? value.GetString() : null;
            if (string.IsNullOrWhiteSpace(message)) return null;
            if (message.Contains("minimum", StringComparison.OrdinalIgnoreCase) || message.Contains("too small", StringComparison.OrdinalIgnoreCase))
                return "el monto es menor al mínimo permitido por Stripe. Use al menos ₡300 para una prueba.";
            if (message.Contains("account", StringComparison.OrdinalIgnoreCase) && message.Contains("activate", StringComparison.OrdinalIgnoreCase))
                return "la cuenta de Stripe aún debe completarse para recibir pagos.";
            return message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [HttpGet("payments/stripe/complete")]
    [Authorize(Policy = "AnyUser")]
    public async Task<IActionResult> CompleteStripeCheckout(string session_id)
    {
        var secret = _configuration["Stripe:SecretKey"];
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(session_id)) return Redirect("/Client/Orders?stripe=error");
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{secret}:")));
        var response = await client.GetAsync($"https://api.stripe.com/v1/checkout/sessions/{Uri.EscapeDataString(session_id)}");
        if (!response.IsSuccessStatusCode) return Redirect("/Client/Orders?stripe=error");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var session = document.RootElement;
        var metadata = session.TryGetProperty("metadata", out var meta) ? meta : default;
        var owner = metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("email", out var ownerEmail) ? ownerEmail.GetString() : null;
        var orders = metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("orders", out var orderIds) ? orderIds.GetString() : null;
        var expectedTotal = string.IsNullOrWhiteSpace(orders) || string.IsNullOrWhiteSpace(owner)
            ? null
            : await GetExpectedOrderTotalAsync(orders, owner);
        if (expectedTotal is null || !HasVerifiedStripePayment(session, expectedTotal.Value) ||
            (User.IsInRole("Cliente") && !string.Equals(owner, CurrentUserEmail, StringComparison.OrdinalIgnoreCase)))
            return Redirect("/Client/Orders?stripe=error");
        foreach (var orderId in orders!.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(value => int.TryParse(value, out var id) ? id : 0).Where(id => id > 0))
            await _sqlStore.MarkOrderPaidAsync(orderId, "Tarjeta", CurrentUserEmail);
        return Redirect("/Client/Orders?stripe=success");
    }

    [HttpPost("payments/paypal/checkout")]
    [Authorize(Policy = "AnyUser")]
    public async Task<IActionResult> StartPayPalCheckout([FromBody] StripeCheckoutRequest? request)
    {
        var clientId = _configuration["PayPal:ClientId"];
        var secret = _configuration["PayPal:Secret"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret))
            return BadRequest(new { message = "PayPal todavía no está configurado en el servidor." });

        var ids = (request?.OrderIds ?? []).Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return BadRequest(new { message = "No hay pedidos válidos para cobrar." });

        var email = CurrentUserEmail ?? string.Empty;
        var rawOrders = JsonSerializer.SerializeToElement(await _sqlStore.OrdersAsync(User.IsInRole("Cliente") ? email : null));
        var selected = rawOrders.EnumerateArray()
            .Where(order => order.TryGetProperty("id", out var id) && ids.Contains(id.GetInt32()))
            .Where(order => !order.TryGetProperty("paymentStatus", out var status) || !string.Equals(status.GetString(), "Pagado", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (selected.Length != ids.Length) return BadRequest(new { message = "Uno de los pedidos no existe, no pertenece al usuario o ya fue pagado." });

        const string baseUrl = "https://api-m.paypal.com";
        var accessToken = await GetPayPalAccessTokenAsync(baseUrl, clientId, secret);
        if (string.IsNullOrWhiteSpace(accessToken)) return BadRequest(new { message = "PayPal no pudo autenticar la pasarela." });

        var total = selected.Sum(order => order.GetProperty("total").GetDecimal());
        var configuredCurrency = (_configuration["PayPal:Currency"] ?? "USD").Trim().ToUpperInvariant();
        // PayPal Checkout no admite CRC para esta cuenta. Convertimos únicamente el cobro de PayPal,
        // manteniendo los pedidos y reportes internos en colones.
        var currency = configuredCurrency == "CRC" ? "USD" : configuredCurrency;
        var exchangeRate = decimal.TryParse(_configuration["PayPal:UsdExchangeRate"], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var configuredRate) && configuredRate > 0
            ? configuredRate
            : 520m;
        var paypalTotal = currency == "USD"
            ? Math.Max(0.01m, decimal.Round(total / exchangeRate, 2, MidpointRounding.AwayFromZero))
            : total;
        var origin = $"{Request.Scheme}://{Request.Host}";
        var payload = new
        {
            intent = "CAPTURE",
            purchase_units = new[]
            {
                new
                {
                    // Conservamos el importe exacto que se envía a PayPal. De ese modo la
                    // confirmación no vuelve a recalcular una conversión distinta después
                    // de que el proveedor ya aprobó el cobro.
                    custom_id = $"orders={string.Join(',', ids)};email={email};paypal_amount={FormatPayPalAmount(paypalTotal, currency)};paypal_currency={currency}",
                    description = $"Pedidos BakeSmart #{string.Join(", ", ids)}",
                    amount = new { currency_code = currency, value = FormatPayPalAmount(paypalTotal, currency) }
                }
            },
            application_context = new
            {
                return_url = $"{origin}/api/payments/paypal/complete",
                cancel_url = $"{origin}/Client/Orders?paypal=cancelled",
                user_action = "PAY_NOW",
                shipping_preference = "NO_SHIPPING"
            }
        };

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.PostAsJsonAsync($"{baseUrl}/v2/checkout/orders", payload);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("PayPal rechazó crear el cobro para los pedidos {Orders}. Estado: {Status}; detalle: {Detail}", string.Join(',', ids), response.StatusCode, errorBody);
            var detail = TryGetPayPalErrorDetail(errorBody);
            return BadRequest(new { message = string.IsNullOrWhiteSpace(detail) ? "PayPal no pudo iniciar el cobro." : $"PayPal rechazó el cobro: {detail}" });
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var approvalLink = document.RootElement.GetProperty("links").EnumerateArray()
            .FirstOrDefault(link => link.TryGetProperty("rel", out var rel) &&
                (string.Equals(rel.GetString(), "payer-action", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(rel.GetString(), "approve", StringComparison.OrdinalIgnoreCase)));
        var approvalUrl = approvalLink.ValueKind == JsonValueKind.Object && approvalLink.TryGetProperty("href", out var href)
            ? href.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(approvalUrl))
            return BadRequest(new { message = "PayPal no devolvió el enlace de autorización." });
        return Ok(new { url = approvalUrl });
    }

    [HttpGet("payments/paypal/complete")]
    [AllowAnonymous]
    public async Task<IActionResult> CompletePayPalCheckout(string token, string? PayerID)
    {
        var clientId = _configuration["PayPal:ClientId"];
        var secret = _configuration["PayPal:Secret"];
        // PayPal v2 puede redirigir sin PayerID; el token aprobado es suficiente para consultar y capturar la orden.
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret))
            return Redirect("/Client/Orders?paypal=error=missing-context");

        const string baseUrl = "https://api-m.paypal.com";
        var accessToken = await GetPayPalAccessTokenAsync(baseUrl, clientId, secret);
        if (string.IsNullOrWhiteSpace(accessToken)) return Redirect("/Client/Orders?paypal=error=authentication");

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var approved = await client.GetAsync($"{baseUrl}/v2/checkout/orders/{Uri.EscapeDataString(token)}");
        if (!approved.IsSuccessStatusCode)
        {
            _logger.LogWarning("PayPal no devolvió el pedido {Token}. Estado: {Status}", token, approved.StatusCode);
            return Redirect("/Client/Orders?paypal=error=order-lookup");
        }
        using var approvalDocument = JsonDocument.Parse(await approved.Content.ReadAsStringAsync());
        var customerEmail = User.IsInRole("Cliente") ? CurrentUserEmail : null;
        var paypalStatus = approvalDocument.RootElement.TryGetProperty("status", out var status)
            ? status.GetString()
            : null;

        // Según el método usado por PayPal, el regreso puede llegar con la orden
        // aprobada (hay que capturarla) o ya completada (solo hay que validarla).
        // Ambos caminos verifican propietario, importe y captura antes de cambiar
        // cualquier pedido a pagado.
        if (string.Equals(paypalStatus, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            var alreadyConfirmed = await ConfirmPayPalOrderAsync(approvalDocument.RootElement, customerEmail);
            if (!alreadyConfirmed) _logger.LogWarning("PayPal devolvió el pedido {Token} como completado, pero no superó la validación local.", token);
            return Redirect(alreadyConfirmed ? "/Client/Orders?paypal=success" : $"/Client/Orders?paypal=processing&token={Uri.EscapeDataString(token)}");
        }

        if (!IsApprovedPayPalOrderForCustomer(approvalDocument.RootElement, customerEmail))
        {
            _logger.LogWarning("PayPal devolvió el pedido {Token} sin aprobación válida. Estado: {Status}", token, paypalStatus);
            return Redirect($"/Client/Orders?paypal=error={Uri.EscapeDataString(paypalStatus ?? "not-approved")}");
        }
        // PayPal requiere Content-Type application/json en /capture. Enviar null produce
        // HTTP 415 ("The request payload is not supported") en las cuentas Live.
        using var captureRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v2/checkout/orders/{Uri.EscapeDataString(token)}/capture")
        {
            Content = JsonContent.Create(new { })
        };
        var capture = await client.SendAsync(captureRequest);
        if (!capture.IsSuccessStatusCode)
        {
            _logger.LogWarning("PayPal no pudo capturar el pedido {Token}. Estado: {Status}; detalle: {Detail}", token, capture.StatusCode, await capture.Content.ReadAsStringAsync());
            return Redirect("/Client/Orders?paypal=error=capture");
        }
        using var document = JsonDocument.Parse(await capture.Content.ReadAsStringAsync());
        var confirmed = await ConfirmPayPalOrderAsync(document.RootElement, customerEmail);
        if (!confirmed) _logger.LogWarning("PayPal capturó el pedido {Token}, pero el importe o propietario no coincidió.", token);
        return Redirect(confirmed ? "/Client/Orders?paypal=success" : $"/Client/Orders?paypal=processing&token={Uri.EscapeDataString(token)}");
    }

    // Segundo camino de conciliacion: la pantalla consulta la orden firmada
    // directamente en PayPal si el retorno perdio la sesion o fue transitorio.
    [HttpGet("payments/paypal/status")]
    [AllowAnonymous]
    public async Task<IActionResult> PayPalPaymentStatus(string token)
    {
        var clientId = _configuration["PayPal:ClientId"];
        var secret = _configuration["PayPal:Secret"];
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret))
            return BadRequest(new { confirmed = false });

        const string baseUrl = "https://api-m.paypal.com";
        var accessToken = await GetPayPalAccessTokenAsync(baseUrl, clientId, secret);
        if (string.IsNullOrWhiteSpace(accessToken))
            return StatusCode(StatusCodes.Status502BadGateway, new { confirmed = false });

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.GetAsync($"{baseUrl}/v2/checkout/orders/{Uri.EscapeDataString(token)}");
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("No se pudo consultar el estado PayPal {Token}. Estado: {Status}", token, response.StatusCode);
            return StatusCode(StatusCodes.Status502BadGateway, new { confirmed = false });
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var status = document.RootElement.TryGetProperty("status", out var statusProperty) ? statusProperty.GetString() : null;
        var confirmed = string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase) &&
            await ConfirmPayPalOrderAsync(document.RootElement, null);
        return Ok(new { confirmed, status });
    }

    [HttpPost("payments/paypal/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> PayPalWebhook()
    {
        var clientId = _configuration["PayPal:ClientId"];
        var secret = _configuration["PayPal:Secret"];
        var webhookId = _configuration["PayPal:WebhookId"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(webhookId))
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync();
        JsonDocument eventDocument;
        try { eventDocument = JsonDocument.Parse(payload); }
        catch (JsonException) { return BadRequest(); }
        using (eventDocument)
        {
            const string baseUrl = "https://api-m.paypal.com";
            var accessToken = await GetPayPalAccessTokenAsync(baseUrl, clientId, secret);
            if (string.IsNullOrWhiteSpace(accessToken)) return StatusCode(StatusCodes.Status503ServiceUnavailable);

            var verification = new
            {
                auth_algo = Request.Headers["PAYPAL-AUTH-ALGO"].ToString(),
                cert_url = Request.Headers["PAYPAL-CERT-URL"].ToString(),
                transmission_id = Request.Headers["PAYPAL-TRANSMISSION-ID"].ToString(),
                transmission_sig = Request.Headers["PAYPAL-TRANSMISSION-SIG"].ToString(),
                transmission_time = Request.Headers["PAYPAL-TRANSMISSION-TIME"].ToString(),
                webhook_id = webhookId,
                webhook_event = eventDocument.RootElement
            };
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var verified = await client.PostAsJsonAsync($"{baseUrl}/v1/notifications/verify-webhook-signature", verification);
            if (!verified.IsSuccessStatusCode) return Unauthorized();
            using var verificationDocument = JsonDocument.Parse(await verified.Content.ReadAsStringAsync());
            if (!verificationDocument.RootElement.TryGetProperty("verification_status", out var status) || status.GetString() != "SUCCESS") return Unauthorized();

            var root = eventDocument.RootElement;
            if (!root.TryGetProperty("event_type", out var type) || type.GetString() != "PAYMENT.CAPTURE.COMPLETED") return Ok();
            if (!root.TryGetProperty("resource", out var resource) || !resource.TryGetProperty("supplementary_data", out var supplementary) ||
                !supplementary.TryGetProperty("related_ids", out var related) || !related.TryGetProperty("order_id", out var orderId)) return BadRequest();

            var paypalOrderId = orderId.GetString();
            if (string.IsNullOrWhiteSpace(paypalOrderId)) return BadRequest();
            var orderResponse = await client.GetAsync($"{baseUrl}/v2/checkout/orders/{Uri.EscapeDataString(paypalOrderId)}");
            if (!orderResponse.IsSuccessStatusCode) return BadRequest();
            using var orderDocument = JsonDocument.Parse(await orderResponse.Content.ReadAsStringAsync());
            return await ConfirmPayPalOrderAsync(orderDocument.RootElement, null) ? Ok() : BadRequest();
        }
    }

    [HttpPost("payments/stripe/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> StripeWebhook()
    {
        var webhookSecret = _configuration["Stripe:WebhookSecret"];
        if (string.IsNullOrWhiteSpace(webhookSecret)) return StatusCode(StatusCodes.Status503ServiceUnavailable);

        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync();
        if (!Request.Headers.TryGetValue("Stripe-Signature", out var signature) || !IsValidStripeSignature(payload, signature.ToString(), webhookSecret))
            return Unauthorized();

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "checkout.session.completed") return Ok();
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("object", out var session)) return BadRequest();
        if (!HasVerifiedStripePayment(session, null)) return Ok();

        var metadata = session.TryGetProperty("metadata", out var value) ? value : default;
        var orders = metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("orders", out var orderIds) ? orderIds.GetString() : null;
        var owner = metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("email", out var ownerEmail) ? ownerEmail.GetString() : null;
        var expectedTotal = string.IsNullOrWhiteSpace(orders) || string.IsNullOrWhiteSpace(owner)
            ? null
            : await GetExpectedOrderTotalAsync(orders, owner);
        if (expectedTotal is null || !HasVerifiedStripePayment(session, expectedTotal.Value)) return BadRequest();

        foreach (var orderId in orders!.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(value => int.TryParse(value, out var id) ? id : 0).Where(id => id > 0))
            await _sqlStore.MarkOrderPaidAsync(orderId, "Tarjeta", owner);
        return Ok();
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
        var normalizedFormat = format.ToLowerInvariant();
        if (normalizedFormat is not "xlsx" and not "pdf")
            return BadRequest(new { message = "Formato de reporte no soportado." });

        await _sqlStore.AddAuditLogAsync(
            "DESCARGAR_REPORTE",
            $"Descargó reporte de {safeType} en formato {normalizedFormat.ToUpperInvariant()} ({start:yyyy-MM-dd} a {end:yyyy-MM-dd})",
            CurrentUserEmail);

        return normalizedFormat switch
        {
            "xlsx" => File(_reportExportService.CreateExcel(type, report, start, end), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"reporte_{safeType}_{stamp}.xlsx"),
            "pdf" => File(_reportExportService.CreatePdf(type, report, start, end), "application/pdf", $"reporte_{safeType}_{stamp}.pdf"),
            _ => BadRequest()
        };
    }

    public sealed record UpdateOrderStatusRequest(string Status);
    public sealed record MarkPaidRequest(string Method);
    public sealed record OpenCashSessionRequest(decimal Amount);
    public sealed record CloseCashSessionRequest(int Id, decimal DeclaredAmount);
    public sealed record AccountingCloseRequest(string? Type);
    public sealed record StripeCheckoutRequest(IReadOnlyList<int>? OrderIds);
    public sealed record ForgotPasswordRequest(string Email);
    public sealed record ContactMessageRequest(string Name, string Email, string? Phone, string? Subject, string Message);
    public sealed record NewsletterRequest(string? Email);

    private static bool IsValidStripeSignature(string payload, string header, string secret)
    {
        var parts = header.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2)
            .ToArray();
        var timestamp = parts.FirstOrDefault(part => part[0] == "t")?.ElementAtOrDefault(1);
        if (!long.TryParse(timestamp, out var unix) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix) > 300) return false;
        var signedPayload = $"{timestamp}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload)));
        return parts.Where(part => part[0] == "v1").Select(part => part[1]).Any(candidate =>
            candidate.Length == expected.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(expected)));
    }

    private async Task<string?> GetPayPalAccessTokenAsync(string baseUrl, string clientId, string secret)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}")));
        var response = await client.PostAsync($"{baseUrl}/v1/oauth2/token", new FormUrlEncodedContent([new KeyValuePair<string, string>("grant_type", "client_credentials")]));
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("access_token", out var token) ? token.GetString() : null;
    }

    private static string FormatPayPalAmount(decimal amount, string currency)
    {
        // PayPal requiere importes sin decimales para CRC y otras monedas de cero dígitos.
        var zeroDecimalCurrencies = new[] { "CRC", "CLP", "HUF", "JPY", "TWD" };
        return zeroDecimalCurrencies.Contains(currency, StringComparer.OrdinalIgnoreCase)
            ? decimal.Truncate(amount).ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            : amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? TryGetPayPalErrorDetail(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array && details.GetArrayLength() > 0 &&
                details[0].TryGetProperty("description", out var description))
                return description.GetString();
            return root.TryGetProperty("message", out var message) ? message.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<bool> ConfirmPayPalOrderAsync(JsonElement order, string? expectedCustomerEmail)
    {
        if (!order.TryGetProperty("status", out var paymentStatus) || paymentStatus.GetString() != "COMPLETED" ||
            !order.TryGetProperty("purchase_units", out var units) || units.GetArrayLength() == 0) return false;
        var unit = units[0];
        var customId = unit.TryGetProperty("custom_id", out var custom) ? custom.GetString() : null;
        var values = (customId ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('=', 2)).Where(item => item.Length == 2)
            .ToDictionary(item => item[0], item => item[1], StringComparer.OrdinalIgnoreCase);
        // El regreso de PayPal puede llegar sin la misma sesión/cookie del
        // navegador que inició el checkout. La propiedad custom_id viaja con
        // la orden capturada y es la referencia autoritativa para ubicar al
        // cliente y sus pedidos; no debemos rechazar un cobro completado solo
        // porque la cookie de retorno no se restauró.
        if (!values.TryGetValue("orders", out var orders) || !values.TryGetValue("email", out var owner) || string.IsNullOrWhiteSpace(owner)) return false;
        var checkoutAmount = 0m;
        var checkoutCurrency = string.Empty;
        var hasExactCheckoutAmount = values.TryGetValue("paypal_amount", out var checkoutAmountText) &&
            values.TryGetValue("paypal_currency", out checkoutCurrency) &&
            decimal.TryParse(checkoutAmountText, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out checkoutAmount);
        if (hasExactCheckoutAmount)
        {
            if (!HasCompletedPayPalCapture(unit)) return false;
            if (!HasVerifiedPayPalCapture(unit, checkoutAmount, checkoutCurrency ?? string.Empty))
                _logger.LogWarning("PayPal confirmó la captura de los pedidos {Orders}, pero devolvió un importe diferente al registrado en checkout. Se conciliará por la referencia interna de PayPal.", orders);
        }
        else
        {
            var expectedTotal = string.IsNullOrWhiteSpace(owner) ? null : await GetExpectedOrderTotalAsync(orders, owner);
            if (!HasCompletedPayPalCapture(unit)) return false;
            if (expectedTotal is null || !HasVerifiedPayPalCapture(unit, expectedTotal.Value))
                _logger.LogWarning("PayPal confirmó la captura de los pedidos {Orders}, pero no fue posible comparar el importe histórico. Se conciliará por la referencia interna de PayPal.", orders);
        }
        foreach (var orderId in orders.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(value => int.TryParse(value, out var id) ? id : 0).Where(id => id > 0))
            await _sqlStore.MarkOrderPaidAsync(orderId, "PayPal", owner);
        return true;
    }

    private async Task<decimal?> GetExpectedOrderTotalAsync(string rawOrderIds, string customerEmail)
    {
        var ids = rawOrderIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.TryParse(value, out var id) ? id : 0)
            .Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0 || string.IsNullOrWhiteSpace(customerEmail)) return null;

        var allOrders = JsonSerializer.SerializeToElement(await _sqlStore.OrdersAsync(customerEmail));
        var selected = allOrders.EnumerateArray()
            .Where(order => order.TryGetProperty("id", out var id) && ids.Contains(id.GetInt32()))
            .ToArray();
        return selected.Length == ids.Length
            ? selected.Sum(order => order.GetProperty("total").GetDecimal())
            : null;
    }

    private static bool HasVerifiedStripePayment(JsonElement session, decimal? expectedTotal)
    {
        var complete = session.TryGetProperty("status", out var status) && string.Equals(status.GetString(), "complete", StringComparison.OrdinalIgnoreCase);
        var paid = session.TryGetProperty("payment_status", out var paymentStatus) && string.Equals(paymentStatus.GetString(), "paid", StringComparison.OrdinalIgnoreCase);
        var paymentMode = session.TryGetProperty("mode", out var mode) && string.Equals(mode.GetString(), "payment", StringComparison.OrdinalIgnoreCase);
        if (!complete || !paid || !paymentMode) return false;
        if (expectedTotal is null) return true;
        return session.TryGetProperty("amount_total", out var amount) && amount.TryGetInt64(out var totalInCents) &&
            totalInCents == decimal.ToInt64(decimal.Round(expectedTotal.Value * 100m, 0, MidpointRounding.AwayFromZero));
    }

    private bool HasVerifiedPayPalCapture(JsonElement unit, decimal expectedTotal)
    {
        if (!unit.TryGetProperty("payments", out var payments) || !payments.TryGetProperty("captures", out var captures) || captures.GetArrayLength() == 0 ||
            !captures[0].TryGetProperty("status", out var captureStatus) || !string.Equals(captureStatus.GetString(), "COMPLETED", StringComparison.OrdinalIgnoreCase)) return false;

        // La respuesta de captura Live trae el importe confirmado dentro de
        // payments.captures[].amount. Algunas respuestas no repiten amount en
        // purchase_units[], por lo que validar solo ese campo dejaba cobros
        // reales sin conciliar localmente.
        var capture = captures[0];
        var amount = capture.TryGetProperty("amount", out var capturedAmount)
            ? capturedAmount
            : unit.TryGetProperty("amount", out var unitAmount) ? unitAmount : default;
        if (amount.ValueKind != JsonValueKind.Object || !amount.TryGetProperty("currency_code", out var currencyCode) || !amount.TryGetProperty("value", out var value)) return false;

        var configuredCurrency = (_configuration["PayPal:Currency"] ?? "USD").ToUpperInvariant();
        var currency = configuredCurrency == "CRC" ? "USD" : configuredCurrency;
        var rate = decimal.TryParse(_configuration["PayPal:UsdExchangeRate"], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var configuredRate) && configuredRate > 0 ? configuredRate : 520m;
        var expected = currency == "USD" ? decimal.Round(expectedTotal / rate, 2, MidpointRounding.AwayFromZero) : expectedTotal;
        return string.Equals(currencyCode.GetString(), currency, StringComparison.OrdinalIgnoreCase) &&
            decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var paidAmount) &&
            Math.Abs(paidAmount - expected) <= 0.01m;
    }

    private static bool HasVerifiedPayPalCapture(JsonElement unit, decimal expectedAmount, string expectedCurrency)
    {
        if (!unit.TryGetProperty("payments", out var payments) || !payments.TryGetProperty("captures", out var captures) || captures.GetArrayLength() == 0 ||
            !captures[0].TryGetProperty("status", out var captureStatus) || !string.Equals(captureStatus.GetString(), "COMPLETED", StringComparison.OrdinalIgnoreCase)) return false;

        var capture = captures[0];
        if (!capture.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Object ||
            !amount.TryGetProperty("currency_code", out var currency) || !amount.TryGetProperty("value", out var value)) return false;

        return string.Equals(currency.GetString(), expectedCurrency, StringComparison.OrdinalIgnoreCase) &&
            decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var paidAmount) &&
            Math.Abs(paidAmount - expectedAmount) <= 0.01m;
    }

    private static bool HasCompletedPayPalCapture(JsonElement unit)
    {
        return unit.TryGetProperty("payments", out var payments) &&
               payments.TryGetProperty("captures", out var captures) &&
               captures.GetArrayLength() > 0 &&
               captures[0].TryGetProperty("status", out var captureStatus) &&
               string.Equals(captureStatus.GetString(), "COMPLETED", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApprovedPayPalOrderForCustomer(JsonElement order, string? expectedCustomerEmail)
    {
        if (!order.TryGetProperty("status", out var status) || !string.Equals(status.GetString(), "APPROVED", StringComparison.OrdinalIgnoreCase) ||
            !order.TryGetProperty("purchase_units", out var units) || units.GetArrayLength() == 0) return false;
        if (string.IsNullOrWhiteSpace(expectedCustomerEmail)) return true;
        var customId = units[0].TryGetProperty("custom_id", out var custom) ? custom.GetString() : null;
        return (customId ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('=', 2)).Any(item => item.Length == 2 && string.Equals(item[0], "email", StringComparison.OrdinalIgnoreCase) && string.Equals(item[1], expectedCustomerEmail, StringComparison.OrdinalIgnoreCase));
    }

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
