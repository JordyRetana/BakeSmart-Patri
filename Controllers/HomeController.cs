using BakeSmartPatri.Data;
using BakeSmartPatri.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;

namespace BakeSmartPatri.Controllers
{
    public class HomeController : Controller
    {
        private readonly SqlStore _sqlStore;

        public HomeController(SqlStore sqlStore)
        {
            _sqlStore = sqlStore;
        }

        [OutputCache(Duration = 60)]
        public async Task<IActionResult> Index()
        {
            var productsTask = _sqlStore.CatalogProductsAsync();
            var categoriesTask = _sqlStore.CatalogCategoriesAsync();
            await Task.WhenAll(productsTask, categoriesTask);
            return View(new CatalogIndexViewModel(await categoriesTask, await productsTask));
        }

        public IActionResult About() => View();

        public IActionResult Contact() => View();

        public IActionResult Error() => View();
    }
}
