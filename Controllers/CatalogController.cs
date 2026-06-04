using BakeSmartPatri.Data;
using BakeSmartPatri.Models;
using Microsoft.AspNetCore.Mvc;

namespace BakeSmartPatri.Controllers
{
    public class CatalogController : Controller
    {
        private readonly SqlStore _sqlStore;

        public CatalogController(SqlStore sqlStore)
        {
            _sqlStore = sqlStore;
        }

        public async Task<IActionResult> Index()
        {
            var model = new CatalogIndexViewModel(
                await _sqlStore.CatalogCategoriesAsync(),
                await _sqlStore.CatalogProductsAsync());

            return View(model);
        }

        public async Task<IActionResult> Details(int id)
        {
            var model = await _sqlStore.CatalogProductDetailsAsync(id);
            return model is null ? NotFound() : View(model);
        }
    }
}
