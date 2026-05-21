using BakeSmartPatri.Data;
using Microsoft.AspNetCore.Mvc;

namespace BakeSmartPatri.Controllers;

[Route("api")]
public class ApiController : Controller
{
    private readonly SqlStore _sqlStore;

    public ApiController(SqlStore sqlStore)
    {
        _sqlStore = sqlStore;
    }

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
    public async Task<IActionResult> Dashboard()
    {
        if (_sqlStore.IsEnabled)
            return Json(await _sqlStore.DashboardAsync());

        return Json(new
        {
            kpis = new[]
            {
                new { label = "Pedidos hoy", value = 18, delta = "+12%" },
                new { label = "En producción", value = 7, delta = "+2" },
                new { label = "Ventas (₡)", value = 245000, delta = "+8%" },
                new { label = "Alertas inventario", value = 3, delta = "stock bajo" }
            }
        });
    }

    [HttpGet("orders")]
    public async Task<IActionResult> Orders()
    {
        if (_sqlStore.IsEnabled)
            return Json(await _sqlStore.OrdersAsync());

        var rows = new[]
        {
            new { id=1012, cliente="María Gómez", producto="Cake Red Velvet (1.5kg)", estado="En Producción", entrega="2026-02-18", total=32000, canal="Web" },
            new { id=1013, cliente="Clínica Santa Ana", producto="120 cupcakes corporativos", estado="Confirmado", entrega="2026-02-19", total=98000, canal="WhatsApp" },
            new { id=1014, cliente="José Mora", producto="Cheesecake maracuyá", estado="Listo", entrega="2026-02-16", total=18000, canal="Tienda" },
            new { id=1015, cliente="Ana Solís", producto="Cake infantil (tema unicornio)", estado="Pendiente Pago", entrega="2026-02-20", total=45000, canal="Instagram" },
        };
        return Json(rows);
    }

    [HttpGet("inventory")]
    public async Task<IActionResult> Inventory()
    {
        if (_sqlStore.IsEnabled)
            return Json(await _sqlStore.InventoryAsync());

        var rows = new[]
        {
            new { sku="HAR-001", item="Harina 000", unidad="kg", stock=8, min=10, costo=900, proveedor="Distribuidora Central" },
            new { sku="AZU-002", item="Azúcar", unidad="kg", stock=14, min=12, costo=820, proveedor="Dulce Tico" },
            new { sku="HUE-010", item="Huevo", unidad="unidad", stock=120, min=150, costo=95, proveedor="Avícola SJ" },
            new { sku="MNT-003", item="Mantequilla", unidad="kg", stock=3, min=6, costo=2600, proveedor="Lácteos del Valle" },
        };
        return Json(rows);
    }
}
