using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BakeSmartPatri.Controllers
{
    public class AccountController : Controller
    {
        private static readonly (string email, string pass, string role, string displayName)[] Users =
        {
            ("admin@demo.com",   "1234", "Admin",   "Admin Demo"),
            ("staff@demo.com",   "1234", "Staff",   "Staff Demo"),
            ("cliente@demo.com", "1234", "Cliente", "Cliente Demo"),
        };

        [HttpGet]
        public IActionResult Login(string? returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl ?? "";
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(string email, string password, string? returnUrl = null)
        {
            email = (email ?? "").Trim().ToLowerInvariant();
            password = password ?? "";

            var u = Users.FirstOrDefault(x => x.email.Equals(email, StringComparison.OrdinalIgnoreCase) && x.pass == password);
            if (string.IsNullOrWhiteSpace(u.email))
            {
                TempData["Toast"] = "Credenciales inválidas (demo).";
                ViewData["ReturnUrl"] = returnUrl ?? "";
                return View();
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, u.email),
                new(ClaimTypes.Name, u.displayName),
                new(ClaimTypes.Email, u.email),
                new(ClaimTypes.Role, u.role),
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    AllowRefresh = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
                });

            if (u.role == "Cliente")
                return RedirectToAction("Index", "Client");

            return RedirectToAction("Index", "Dashboard");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Index", "Home");
        }

        public IActionResult Denied() => View();
    }
}
