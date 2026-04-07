using Microsoft.AspNetCore.Mvc;

namespace BakeSmartPatri.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index() => View();

        public IActionResult Error() => View();
    }
}
