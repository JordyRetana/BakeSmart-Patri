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
            return View(await BuildIndexModelAsync());
        }

        public async Task<IActionResult> Categories() => View(await BuildIndexModelAsync());

        public async Task<IActionResult> Offers() => View(await BuildIndexModelAsync());

        public async Task<IActionResult> Popular() => View(await BuildIndexModelAsync());

        public async Task<IActionResult> New() => View(await BuildIndexModelAsync());

        public async Task<IActionResult> Combos() => View(await BuildIndexModelAsync());

        public async Task<IActionResult> Details(int id)
        {
            var model = await _sqlStore.CatalogProductDetailsAsync(id);
            return model is null ? NotFound() : View(model);
        }

        private async Task<CatalogIndexViewModel> BuildIndexModelAsync()
        {
            return new CatalogIndexViewModel(
                await _sqlStore.CatalogCategoriesAsync(),
                await _sqlStore.CatalogProductsAsync());
        }
    }
}
