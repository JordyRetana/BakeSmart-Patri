using BakeSmartPatri.Data;
using BakeSmartPatri.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

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

        public async Task<IActionResult> Cart()
        {
            return View(await BuildIndexModelAsync());
        }

        public IActionResult Categories() => RedirectToAction(nameof(Index), new { categories = "open" });

        public async Task<IActionResult> Offers()
        {
            var modelTask = BuildIndexModelAsync();
            var promotionsTask = _sqlStore.PromotionsAsync();
            await Task.WhenAll(modelTask, promotionsTask);
            var model = await modelTask;
            var today = DateTime.UtcNow.AddHours(-6).Date;
            var discounts = new Dictionary<int, decimal>();
            foreach (var promotion in JsonSerializer.SerializeToElement(await promotionsTask).EnumerateArray())
            {
                if (!promotion.GetProperty("active").GetBoolean()
                    || promotion.GetProperty("customerIds").GetArrayLength() > 0
                    || string.Equals(promotion.GetProperty("name").GetString()?.Trim(), "Cliente frecuente", StringComparison.OrdinalIgnoreCase)) continue;
                if (DateTime.TryParse(promotion.GetProperty("startDate").GetString(), out var start) && start.Date > today) continue;
                if (DateTime.TryParse(promotion.GetProperty("endDate").GetString(), out var end) && end.Date < today) continue;
                var rate = promotion.GetProperty("discount").GetDecimal();
                var ids = promotion.GetProperty("productIds").EnumerateArray().Select(item => item.GetInt32()).ToArray();
                var targets = ids.Length > 0 ? ids : model.Products.Where(item => item.IsActive).Select(item => item.Id);
                foreach (var id in targets) discounts[id] = Math.Max(discounts.GetValueOrDefault(id), rate);
            }
            ViewBag.OfferDiscounts = discounts;
            return View(model);
        }

        public async Task<IActionResult> Popular() => View(await BuildIndexModelAsync());

        public async Task<IActionResult> New() => View(await BuildIndexModelAsync());

        public async Task<IActionResult> Combos() => View(await _sqlStore.CombosAsync(activeOnly: true));

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
