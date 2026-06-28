using BakeSmartPatri.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BakeSmartPatri.Controllers
{
    public class AccountController : Controller
    {
        private readonly SqlStore _sqlStore;

        public AccountController(SqlStore sqlStore)
        {
            _sqlStore = sqlStore;
        }

        [HttpGet]
        public IActionResult Login(string? returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl ?? "";
            return View();
        }

        [HttpGet]
        public IActionResult Register(string? returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl ?? "";
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(string email, string password, string? returnUrl = null)
        {
            email = (email ?? "").Trim().ToLowerInvariant();
            password ??= "";

            var user = await _sqlStore.AuthenticateAsync(email, password);
            if (user is null)
            {
                TempData["Toast"] = "Credenciales invalidas.";
                ViewData["ReturnUrl"] = returnUrl ?? "";
                return View();
            }

            await _sqlStore.AddAuditLogAsync("LOGIN", $"Inicio de sesion: {email} ({user.Role})", email);
            await SignInUserAsync(user);

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);

            if (user.Role == "Cliente")
                return RedirectToAction("Index", "Client");

            return RedirectToAction("Index", "Dashboard");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(string firstName, string lastName, string email, string? phone, string? addressLine, string password, string confirmPassword, string? returnUrl = null)
        {
            firstName = (firstName ?? "").Trim();
            lastName = (lastName ?? "").Trim();
            email = (email ?? "").Trim().ToLowerInvariant();
            password ??= "";
            confirmPassword ??= "";

            if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName) || string.IsNullOrWhiteSpace(email))
            {
                TempData["Toast"] = "Completa nombre, apellidos y correo.";
                ViewData["ReturnUrl"] = returnUrl ?? "";
                return View();
            }

            if (password.Length < 8)
            {
                TempData["Toast"] = "La contraseña debe tener al menos 8 caracteres.";
                ViewData["ReturnUrl"] = returnUrl ?? "";
                return View();
            }

            if (password != confirmPassword)
            {
                TempData["Toast"] = "Las contraseñas no coinciden.";
                ViewData["ReturnUrl"] = returnUrl ?? "";
                return View();
            }

            try
            {
                await _sqlStore.RegisterCustomerAsync(new SqlStore.RegisterCustomerInput(firstName, lastName, email, phone, addressLine, password));
            }
            catch (Exception ex)
            {
                TempData["Toast"] = ex.Message.Contains("Ya existe", StringComparison.OrdinalIgnoreCase)
                    ? "Ya existe un usuario con ese correo."
                    : "No se pudo completar el registro.";
                ViewData["ReturnUrl"] = returnUrl ?? "";
                return View();
            }

            var user = await _sqlStore.AuthenticateAsync(email, password);
            if (user is null)
            {
                TempData["Toast"] = "Usuario creado. Inicia sesion con tus credenciales.";
                return RedirectToAction(nameof(Login));
            }

            await SignInUserAsync(user);

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);

            return RedirectToAction("Index", "Client");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            var email = User.FindFirst(ClaimTypes.Email)?.Value ?? "";
            if (!string.IsNullOrWhiteSpace(email))
                await _sqlStore.AddAuditLogAsync("LOGOUT", $"Cierre de sesion: {email}", email);

            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Index", "Home");
        }

        [HttpGet]
        [Microsoft.AspNetCore.Authorization.Authorize]
        public async Task<IActionResult> Profile()
        {
            var email = User.FindFirst(ClaimTypes.Email)?.Value ?? "";
            var profile = await _sqlStore.GetProfileAsync(email);
            if (profile is null)
                return RedirectToAction(nameof(Login));

            ViewData["Profile"] = profile;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Microsoft.AspNetCore.Authorization.Authorize]
        public async Task<IActionResult> Profile(
            string firstName, string lastName,
            string? phone, string? address,
            string? newPassword, string? confirmPassword,
            int? customerAddressId, string? addressLabel,
            decimal? latitude, decimal? longitude)
        {
            var email = User.FindFirst(ClaimTypes.Email)?.Value ?? "";

            firstName = (firstName ?? "").Trim();
            lastName  = (lastName  ?? "").Trim();

            if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
            {
                TempData["ToastError"] = "El nombre y apellido son obligatorios.";
                return RedirectToAction(nameof(Profile));
            }

            if (User.IsInRole("Cliente") &&
                !string.IsNullOrWhiteSpace(address) &&
                !SqlStore.HasValidCoordinates(latitude, longitude))
            {
                TempData["ToastError"] = "Debe seleccionar una ubicacion valida en el mapa para guardar la direccion.";
                return RedirectToAction(nameof(Profile));
            }

            if (!string.IsNullOrWhiteSpace(newPassword))
            {
                if (newPassword.Length < 8)
                {
                    TempData["ToastError"] = "La nueva contraseña debe tener al menos 8 caracteres.";
                    return RedirectToAction(nameof(Profile));
                }
                if (newPassword != confirmPassword)
                {
                    TempData["ToastError"] = "Las contraseñas no coinciden.";
                    return RedirectToAction(nameof(Profile));
                }
            }

            await _sqlStore.UpdateProfileAsync(email, new SqlStore.ProfileInput(
                firstName, lastName, phone, address, newPassword,
                customerAddressId, addressLabel, latitude, longitude));
            await _sqlStore.AddAuditLogAsync("ACTUALIZAR_PERFIL", $"Perfil actualizado: {firstName} {lastName}", email);

            // Re-sign with updated display name
            var currentRole = User.FindFirst(ClaimTypes.Role)?.Value ?? "";
            var updatedUser = new SqlStore.AuthUser(email, currentRole, $"{firstName} {lastName}".Trim());
            await SignInUserAsync(updatedUser);

            TempData["ToastSuccess"] = "Perfil actualizado correctamente.";
            return RedirectToAction(nameof(Profile));
        }

        [HttpGet]
        public IActionResult ForgotPassword()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForgotPassword(string email)
        {
            email = (email ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email))
            {
                TempData["ToastError"] = "Indique su correo electronico.";
                return View();
            }

            var result = await _sqlStore.RequestPasswordResetAsync(email);
            if (result)
            {
                TempData["ToastSuccess"] = "Contrasena restablecida. Revise la bitacora del sistema para obtener la temporal.";
            }
            else
            {
                TempData["Toast"] = "Si el correo esta registrado, recibira instrucciones.";
            }

            return RedirectToAction(nameof(Login));
        }

        public IActionResult Denied() => View();

        private async Task SignInUserAsync(SqlStore.AuthUser user)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Email),
                new(ClaimTypes.Name, user.DisplayName),
                new(ClaimTypes.Email, user.Email),
                new(ClaimTypes.Role, user.Role),
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
        }
    }
}
