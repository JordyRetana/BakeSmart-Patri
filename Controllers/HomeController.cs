using BakeSmartPatri.Data;
using BakeSmartPatri.Models;
using Microsoft.AspNetCore.Mvc;

namespace BakeSmartPatri.Controllers
{
    public class HomeController : Controller
    {
        private readonly SqlStore _sqlStore;

        public HomeController(SqlStore sqlStore)
        {
            _sqlStore = sqlStore;
        }

        public async Task<IActionResult> Index()
        {
            var products = await _sqlStore.CatalogProductsAsync();
            var categories = await _sqlStore.CatalogCategoriesAsync();
            return View(new CatalogIndexViewModel(categories, products));
        }

        public IActionResult Error() => View();
    }
}
