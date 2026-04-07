using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BakeSmartPatri.Controllers
{
    [Authorize(Policy = "StaffOrAdmin")]
    public class InventoryController : Controller
    {
        public IActionResult Index() => View();
    }
}
