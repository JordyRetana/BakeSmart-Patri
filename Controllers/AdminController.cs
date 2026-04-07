using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BakeSmartPatri.Controllers;

[Authorize(Policy = "AdminOnly")]
public class AdminController : Controller
{
    public IActionResult Users() => View();
    public IActionResult Roles() => View();

    [HttpPost]
    public IActionResult Save()
    {
        TempData["toast"] = "Cambios guardados (demo).";
        return Redirect(Request.Headers.Referer.ToString());
    }
}
