using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BakeSmartPatri.Controllers;

[Authorize(Roles = "Admin,Staff,Supervisor")]
public class AuditController : Controller
{
    public IActionResult Index() => View();
}
