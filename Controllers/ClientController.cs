using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BakeSmartPatri.Controllers
{
    [Authorize(Policy = "ClientOnly")]
    public class ClientController : Controller
    {
        public IActionResult Index() => View();

        public IActionResult Orders() => View();

        public IActionResult Track() => View();

        public IActionResult Profile() => View();

        [HttpGet]
        [AllowAnonymous]
        public IActionResult GoHomeByRole()
        {
            if (User?.Identity?.IsAuthenticated ?? false)
            {
                if (User.IsInRole("Cliente")) return RedirectToAction("Index", "Client");
                if (User.IsInRole("Admin") || User.IsInRole("Staff")) return RedirectToAction("Index", "Dashboard");
            }
            return RedirectToAction("Index", "Home");
        }
    }
}
