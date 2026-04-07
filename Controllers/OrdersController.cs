using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BakeSmartPatri.Controllers
{
    [Authorize(Policy = "AnyUser")]
    public class OrdersController : Controller
    {
        public IActionResult Index() => View();

        [Authorize(Policy = "StaffOrAdmin")]
        public IActionResult Create() => View();

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "StaffOrAdmin")]
        public IActionResult Create(string? cliente, string? producto, decimal? total, DateTime? entrega, string? notas)
        {
            TempData["Toast"] = "Pedido creado (demo).";
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
            TempData["Toast"] = "Pedido actualizado (demo).";
            return RedirectToAction(nameof(Details), new { id });
        }

        [HttpGet]
        public IActionResult Data()
        {
            var rows = new[]
            {
                new { id=1012, cliente="María Gómez", producto="Cake Red Velvet (1.5kg)", estado="En Producción", entrega="2026-02-18", total="₡32 000" },
                new { id=1013, cliente="Clínica Santa Fe", producto="120 cupcakes corporativos", estado="Confirmado", entrega="2026-02-19", total="₡84 000" },
                new { id=1014, cliente="Andrea M.", producto="Cheesecake frutos rojos", estado="Pendiente Pago", entrega="2026-02-20", total="₡28 000" },
                new { id=1015, cliente="Luis P.", producto="Cake unicornio", estado="En Producción", entrega="2026-02-20", total="₡45 000" },
            };
            return Json(new { rows });
        }
    }
}
