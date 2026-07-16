using BakeSmartPatri.Data;
using BakeSmartPatri.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;

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
                await Task.WhenAll(productsTask, categoriesTask, settingsTask);
                ViewBag.HomeSettings = await settingsTask;
                return View(new CatalogIndexViewModel(await categoriesTask, await productsTask));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo cargar el contenido dinamico del inicio.");
                ViewBag.HomeSettings = new Dictionary<string, string>();
                return View(new CatalogIndexViewModel([], []));
            }
        }

        public IActionResult About() => View();

        public IActionResult Contact() => View();

        public IActionResult Error() => View();
    }
}
