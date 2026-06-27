using BakeSmartPatri.Data;
using BakeSmartPatri.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

        [Authorize(Policy = "StaffOrAdmin")]
        public async Task<IActionResult> Create()
        {
            var model = new OrderCreateViewModel(
                await _sqlStore.CatalogProductsAsync(),
                await _sqlStore.PaymentMethodNamesAsync());

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "StaffOrAdmin")]
        public async Task<IActionResult> Create(
            string? cliente, string? telefono, string? email,
            int? productoId, DateTime? entrega, string? metodoPago,
            string? direccion, string? notas, string? metodoEntrega,
            decimal? latitudEntrega, decimal? longitudEntrega,
            string? referenciaEntrega, int? customerAddressId)
        {
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

                if (!SqlStore.HasValidCoordinates(latitudEntrega, longitudEntrega))
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
                    Notes: notas?.Trim(),
                    PaymentMethod: metodoPago?.Trim() ?? "Pendiente",
                    DestinationLatitude: latitudEntrega,
                    DestinationLongitude: longitudEntrega,
                    DeliveryReference: referenciaEntrega,
                    CustomerAddressId: customerAddressId,
                    DeliveryMethod: deliveryMethod
                );

                var orderId = await _sqlStore.CreateOrderAsync(input, User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value);
                TempData["ToastSuccess"] = $"Pedido #{orderId} creado correctamente.";
                return RedirectToAction(nameof(Details), new { id = orderId });
            }
            catch (Exception ex)
            {
                TempData["ToastError"] = $"Error al crear el pedido: {ex.Message}";
                return RedirectToAction(nameof(Create));
            }
        }

        public IActionResult Details(int id) => View();

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
