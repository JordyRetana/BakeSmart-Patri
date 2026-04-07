using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BakeSmartPatri.Controllers
{
    [Authorize(Policy = "AdminOnly")]
    public class RolesController : Controller
    {
        public IActionResult Index() => View();
    }
}
