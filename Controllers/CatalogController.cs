using Microsoft.AspNetCore.Mvc;

namespace BakeSmartPatri.Controllers
{
    public class CatalogController : Controller
    {
        public IActionResult Index() => View();

        public IActionResult Details(int id) => View();
    }
}
