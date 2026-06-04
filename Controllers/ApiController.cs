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

    [HttpGet("inventory/movements")]
    public async Task<IActionResult> InventoryMovements() => Json(await _sqlStore.InventoryMovementsAsync());

    [HttpGet("customers")]
    public async Task<IActionResult> Customers() => Json(await _sqlStore.CustomersAsync());

    [HttpGet("promotions")]
    public async Task<IActionResult> Promotions() => Json(await _sqlStore.PromotionsAsync());

    [HttpGet("users")]
    public async Task<IActionResult> Users() => Json(await _sqlStore.UsersAsync());

    [HttpGet("roles")]
    public async Task<IActionResult> Roles() => Json(await _sqlStore.RolesAsync());

    [HttpGet("pos/config")]
    public async Task<IActionResult> PosConfig() => Json(await _sqlStore.PosConfigAsync());

    [HttpGet("logs")]
    public async Task<IActionResult> Logs() => Json(await _sqlStore.AuditLogsAsync());

    [HttpGet("reports/{type}")]
    public async Task<IActionResult> Reports(string type, DateTime? start, DateTime? end)
    {
        return Json(await _sqlStore.ReportsAsync(type, start, end));
    }

    public sealed record UpdateOrderStatusRequest(string Status);
    public sealed record MarkPaidRequest(string Method);
}
