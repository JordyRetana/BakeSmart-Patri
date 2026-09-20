using BakeSmartPatri.Data;
using BakeSmartPatri.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using System.Text.Json;

namespace BakeSmartPatri.Controllers
{
    public class HomeController : Controller
    {
        private readonly SqlStore _sqlStore;
        private readonly ILogger<HomeController> _logger;

        public HomeController(SqlStore sqlStore, ILogger<HomeController> logger)
        {
            _sqlStore = sqlStore;
            _logger = logger;
        }

        [OutputCache(Duration = 60)]
        public async Task<IActionResult> Index()
        {
            try
            {
                var productsTask = _sqlStore.CatalogProductsAsync();
                var categoriesTask = _sqlStore.CatalogCategoriesAsync();
                var settingsTask = _sqlStore.SettingsDictionaryAsync();
                var promotionsTask = _sqlStore.PromotionsAsync();
                await Task.WhenAll(productsTask, categoriesTask, settingsTask, promotionsTask);
                ViewBag.HomeSettings = await settingsTask;
                var today = DateTime.UtcNow.AddHours(-6).Date;
                var discounts = new Dictionary<int, decimal>();
                foreach (var promotion in JsonSerializer.SerializeToElement(await promotionsTask).EnumerateArray())
                {
                    if (!promotion.GetProperty("active").GetBoolean() || promotion.GetProperty("customerIds").GetArrayLength() > 0) continue;
                    if (DateTime.TryParse(promotion.GetProperty("startDate").GetString(), out var start) && start.Date > today) continue;
                    if (DateTime.TryParse(promotion.GetProperty("endDate").GetString(), out var end) && end.Date < today) continue;
                    var rate = promotion.GetProperty("discount").GetDecimal();
                    var ids = promotion.GetProperty("productIds").EnumerateArray().Select(item => item.GetInt32()).ToArray();
                    var targets = ids.Length > 0 ? ids : (await productsTask).Where(item => item.IsActive).Select(item => item.Id);
                    foreach (var id in targets) discounts[id] = Math.Max(discounts.GetValueOrDefault(id), rate);
                }
                ViewBag.HomeOfferDiscounts = discounts;
                return View(new CatalogIndexViewModel(await categoriesTask, await productsTask));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo cargar el contenido dinamico del inicio.");
                ViewBag.HomeSettings = new Dictionary<string, string>();
                ViewBag.HomeOfferDiscounts = new Dictionary<int, decimal>();
                return View(new CatalogIndexViewModel([], []));
            }
        }

        public IActionResult About() => View();

        public IActionResult Terms() => View();

        public IActionResult Privacy() => View();

        public async Task<IActionResult> Contact()
        {
            try
            {
                ViewBag.SiteSettings = await _sqlStore.SettingsDictionaryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo cargar la configuración pública de contacto.");
                ViewBag.SiteSettings = new Dictionary<string, string>();
            }

            return View();
        }

        public IActionResult Error() => View();

        [Route("Home/Status")]
        public IActionResult Status(int code = 500)
        {
            Response.StatusCode = code;
            ViewBag.Code = code;
            return View("~/Views/Shared/Status.cshtml");
        }
    }
}
