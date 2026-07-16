using BakeSmartPatri.Data;
using BakeSmartPatri.Models;
using Microsoft.AspNetCore.Mvc;

namespace BakeSmartPatri.Controllers
{
    public class CatalogController : Controller
    {
        private readonly SqlStore _sqlStore;
        private readonly ILogger<CatalogController> _logger;

        public CatalogController(SqlStore sqlStore, ILogger<CatalogController> logger)
        {
            _sqlStore = sqlStore;
            _logger = logger;
        }

        public async Task<IActionResult> Index()
        {
            return View(await BuildIndexModelAsync());
        }

        public async Task<IActionResult> Favorites()
        {
            return View(await BuildIndexModelAsync());
        }

        public IActionResult Categories() => RedirectToAction(nameof(Index), new { categories = "open" });

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
            try
            {
                return new CatalogIndexViewModel(
                    await _sqlStore.CatalogCategoriesAsync(),
                    await _sqlStore.CatalogProductsAsync());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo cargar el catalogo publico.");
                return new CatalogIndexViewModel([], []);
            }
        }
    }
}
