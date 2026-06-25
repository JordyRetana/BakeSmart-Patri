using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BakeSmartPatri.Controllers //comentar si luego no funciona
{
    [Authorize(Roles = "Admin")]
    public class ContentController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public IActionResult Update(string key, string value)
        {
            // Simulación o integración futura con DB
            TempData["Toast"] = "Contenido actualizado correctamente";
            return RedirectToAction("Index");
        }
    }
}