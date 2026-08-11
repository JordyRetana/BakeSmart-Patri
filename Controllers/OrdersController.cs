using BakeSmartPatri.Data;
using BakeSmartPatri.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text.Json;

namespace BakeSmartPatri.Controllers
{
    [Authorize(Policy = "AnyUser")]
    public class OrdersController : Controller
    {
        private readonly SqlStore _sqlStore;

        public OrdersController(SqlStore sqlStore)
        {
            _sqlStore = sqlStore;
        }

        public IActionResult Index() => View();

        public async Task<IActionResult> Create()
        {
            var model = new OrderCreateViewModel(
                await _sqlStore.CatalogProductsAsync(),
                await _sqlStore.PaymentMethodNamesAsync());

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            string? cliente, string? telefono, string? email,
            int? productoId, DateTime? entrega, string? metodoPago,
            string? direccion, string? notas, string? metodoEntrega,
            string? latitudEntrega, string? longitudEntrega,
            string? referenciaEntrega, int? customerAddressId,
            string? tipoPedido, string? tamano, string? hora, string? sabor,
            string? color, string? mensaje)
        {
            var parsedLatitude = ParseCoordinate(latitudEntrega);
            var parsedLongitude = ParseCoordinate(longitudEntrega);

            if (User.IsInRole("Cliente"))
            {
                var currentEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? string.Empty;
                var profile = await _sqlStore.GetProfileAsync(currentEmail);
                if (profile is null)
                {
                    TempData["ToastError"] = "No se encontro el perfil autenticado.";
                    return RedirectToAction("Login", "Account");
                }

                cliente = $"{profile.FirstName} {profile.LastName}".Trim();
                email = profile.Email;
                telefono = profile.Phone;
            }

            if (string.IsNullOrWhiteSpace(cliente) || string.IsNullOrWhiteSpace(email) || !productoId.HasValue || !entrega.HasValue)
            {
                TempData["ToastError"] = "Complete los campos obligatorios: cliente, email, producto y fecha de entrega.";
                return RedirectToAction(nameof(Create));
            }

            var deliveryMethod = (metodoEntrega ?? "domicilio").Trim().ToLowerInvariant();
            if (deliveryMethod != "retiro")
            {
                if (string.IsNullOrWhiteSpace(direccion))
                {
                    TempData["ToastError"] = "Debe indicar la direccion de entrega.";
                    return RedirectToAction(nameof(Create));
                }

                if (!SqlStore.HasValidCoordinates(parsedLatitude, parsedLongitude))
                {
                    TempData["ToastError"] = "Debe seleccionar una ubicacion valida en el mapa.";
                    return RedirectToAction(nameof(Create));
                }
            }

            try
            {
                var products = await _sqlStore.CatalogProductsAsync();
                var product = products.FirstOrDefault(p => p.Id == productoId.Value);
                if (product is null)
                {
                    TempData["ToastError"] = "El producto seleccionado no existe.";
                    return RedirectToAction(nameof(Create));
                }

                var quantity = 1m;
                var subtotal = product.UnitPrice * quantity;
                var ivaRate = await _sqlStore.GetIvaRateAsync();
                var tax = subtotal * ivaRate;
                var total = subtotal + tax;

                var input = new SqlStore.CreateOrderInput(
                    CustomerName: cliente.Trim(),
                    Email: email.Trim().ToLowerInvariant(),
                    Phone: telefono?.Trim(),
                    ProductId: productoId.Value,
                    Quantity: quantity,
                    UnitPrice: product.UnitPrice,
                    Subtotal: subtotal,
                    Tax: tax,
                    Total: total,
                    DeliveryDate: entrega.Value,
                    Address: direccion?.Trim(),
                    Notes: BuildOrderNotes(tipoPedido, tamano, hora, sabor, color, mensaje, notas),
                    PaymentMethod: metodoPago?.Trim() ?? "Pendiente",
                    DestinationLatitude: parsedLatitude,
                    DestinationLongitude: parsedLongitude,
                    DeliveryReference: referenciaEntrega,
                    CustomerAddressId: customerAddressId,
                    DeliveryMethod: deliveryMethod
                );

                var orderId = await _sqlStore.CreateOrderAsync(input, User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value);
                TempData["ToastSuccess"] = $"Pedido #{orderId} creado correctamente.";
                var onlinePayment = string.Equals(metodoPago?.Trim(), "Tarjeta", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(metodoPago?.Trim(), "PayPal", StringComparison.OrdinalIgnoreCase);
                return RedirectToAction(nameof(Details), new { id = orderId, pay = onlinePayment ? metodoPago : null });
            }
            catch (Exception ex)
            {
                TempData["ToastError"] = $"Error al crear el pedido: {ex.Message}";
                return RedirectToAction(nameof(Create));
            }
        }

        private static decimal? ParseCoordinate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant)) return invariant;
            if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var localized)) return localized;
            return null;
        }

        private static string? BuildOrderNotes(string? tipoPedido, string? tamano, string? hora, string? sabor, string? color, string? mensaje, string? notes)
        {
            var parts = new[]
            {
                ("Tipo de encargo", tipoPedido), ("Tamaño / porciones", tamano), ("Hora solicitada", hora),
                ("Sabor", sabor), ("Color de decoración", color), ("Mensaje", mensaje), ("Notas", notes)
            }
            .Where(item => !string.IsNullOrWhiteSpace(item.Item2))
            .Select(item => $"{item.Item1}: {item.Item2!.Trim()}");
            var result = string.Join(" | ", parts);
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        public async Task<IActionResult> Details(int id)
        {
            var order = await FindOrderAsync(id);
            ViewData["OrderJson"] = order.ValueKind == JsonValueKind.Undefined ? "null" : order.GetRawText();
            return View();
        }

        /// <summary>Checkout dedicado para un pedido ya creado; no mezcla el flujo de POS.</summary>
        public async Task<IActionResult> Checkout(int id)
        {
            var order = await FindOrderAsync(id);
            if (order.ValueKind == JsonValueKind.Undefined)
            {
                TempData["ToastError"] = "No se encontro el pedido solicitado.";
                return RedirectToAction(nameof(Index));
            }

            ViewData["OrderJson"] = order.GetRawText();
            return View();
        }

        private async Task<JsonElement> FindOrderAsync(int id)
        {
            var email = User.IsInRole("Cliente")
                ? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                : null;
            var ordersJson = JsonSerializer.SerializeToElement(await _sqlStore.OrdersAsync(email));
            return ordersJson.EnumerateArray().FirstOrDefault(row => row.TryGetProperty("id", out var value) && value.GetInt32() == id);
        }

        [Authorize(Policy = "StaffOrAdmin")]
        public IActionResult Edit(int id) => View();

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "StaffOrAdmin")]
        public IActionResult Edit(int id, string? estado, DateTime? entrega, string? notas)
        {
            TempData["Toast"] = "Editar pedidos debe completarse desde el flujo del sistema.";
            return RedirectToAction(nameof(Details), new { id });
        }

        [HttpGet]
        public async Task<IActionResult> Data()
        {
            return Json(new { rows = await _sqlStore.OrdersAsync() });
        }
    }
}
