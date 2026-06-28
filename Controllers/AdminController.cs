using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BakeSmartPatri.Controllers;

[Authorize(Policy = "AdminOnly")]
public class AdminController : Controller
{
    public IActionResult Users() => View();
    public IActionResult Roles() => View();
    public IActionResult Settings() => View();

    [HttpPost]
    public IActionResult Save()
    {
        TempData["toast"] = "Esta accion debe guardarse desde el sistema.";
        return Redirect(Request.Headers.Referer.ToString());
    }
}
