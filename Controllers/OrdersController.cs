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
        public IActionResult Create(string? cliente, string? producto, decimal? total, DateTime? entrega, string? notas)
        {
            TempData["Toast"] = "Crear pedidos debe completarse desde el flujo del sistema.";
            return RedirectToAction(nameof(Index));
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
