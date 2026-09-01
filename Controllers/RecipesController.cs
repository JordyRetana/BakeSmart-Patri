using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BakeSmartPatri.Controllers;

[Authorize(Roles = "Admin,Repostero,Supervisor,EncargadoRecetas")]
public sealed class RecipesController : Controller
{
    public IActionResult Index() => View();
}
