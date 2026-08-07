using BakeSmartPatri.Data;
using BakeSmartPatri.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Security.Claims;

namespace BakeSmartPatri.Controllers
{
    public class AccountController : Controller
    {
        private readonly SqlStore _sqlStore;
        private readonly IEmailService _emailService;

        public AccountController(SqlStore sqlStore, IEmailService emailService)
        {
            _sqlStore = sqlStore;
            _emailService = emailService;
        }

        [HttpGet]
        public IActionResult Login(string? returnUrl = null)
        {
            if (User?.Identity?.IsAuthenticated ?? false)
            {
                if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                    return Redirect(returnUrl);

                if (User.IsInRole("Cliente"))
                    return RedirectToAction("Index", "Client");

                return RedirectToAction("Index", "Dashboard");
            }

            DeleteLegacyAuthCookies();
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
        [IgnoreAntiforgeryToken]
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

            DeleteLegacyAuthCookies(includeCurrent: false);
            try
            {
                await _sqlStore.AddAuditLogAsync("LOGIN", $"Inicio de sesion: {email} ({user.Role})", email);
            }
            catch
            {
                // El inicio de sesion no debe bloquearse si la bitacora no esta disponible.
            }
            await SignInUserAsync(user);

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);

            if (user.Role == "Cliente")
                return RedirectToAction("Index", "Client");

            return RedirectToAction("Index", "Dashboard");
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
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
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Logout()
        {
            var email = User.FindFirst(ClaimTypes.Email)?.Value ?? "";
            if (User.IsInRole("Cajero") && await _sqlStore.HasOpenCashSessionAsync(email))
            {
                TempData["CashLogoutBlocked"] = "No puede cerrar sesión mientras tenga una caja abierta. Complete primero el cierre de caja.";
                return RedirectToAction("Index", "Pos");
            }

            if (!string.IsNullOrWhiteSpace(email))
            {
                try
                {
                    await _sqlStore.AddAuditLogAsync("LOGOUT", $"Cierre de sesion: {email}", email);
                }
                catch
                {
                    // El cierre de sesion debe funcionar aunque la bitacora falle.
                }
            }

            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            DeleteLegacyAuthCookies();
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
            string? currentPassword, string? newPassword, string? confirmPassword,
            int? customerAddressId, string? addressLabel,
            string? latitude, string? longitude)
        {
            var email = User.FindFirst(ClaimTypes.Email)?.Value ?? "";
            decimal? latitudeValue = ParseCoordinate(latitude);
            decimal? longitudeValue = ParseCoordinate(longitude);

            firstName = (firstName ?? "").Trim();
            lastName  = (lastName  ?? "").Trim();

            if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
            {
                TempData["ToastError"] = "El nombre y apellido son obligatorios.";
                return RedirectToAction(nameof(Profile));
            }

            if (User.IsInRole("Cliente") &&
                !string.IsNullOrWhiteSpace(address) &&
                !SqlStore.HasValidCoordinates(latitudeValue, longitudeValue))
            {
                TempData["ToastError"] = "Debe seleccionar una ubicacion valida en el mapa para guardar la direccion.";
                return RedirectToAction(nameof(Profile));
            }

            if (!string.IsNullOrWhiteSpace(newPassword))
            {
                if (string.IsNullOrWhiteSpace(currentPassword))
                {
                    TempData["ToastError"] = "Ingrese su contraseña actual.";
                    return RedirectToAction(nameof(Profile));
                }
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
                if (!await _sqlStore.ChangePasswordAsync(email, currentPassword, newPassword))
                {
                    TempData["ToastError"] = "La contraseña actual no es correcta.";
                    return RedirectToAction(nameof(Profile));
                }
            }

            await _sqlStore.UpdateProfileAsync(email, new SqlStore.ProfileInput(
                firstName, lastName, phone, address, null,
                customerAddressId, addressLabel, latitudeValue, longitudeValue));
            await _sqlStore.AddAuditLogAsync("ACTUALIZAR_PERFIL", $"Perfil actualizado: {firstName} {lastName}", email);

            // Re-sign with updated display name
            var currentRole = User.FindFirst(ClaimTypes.Role)?.Value ?? "";
            var updatedUser = new SqlStore.AuthUser(email, currentRole, $"{firstName} {lastName}".Trim());
            await SignInUserAsync(updatedUser);

            TempData["ToastSuccess"] = "Perfil actualizado correctamente.";
            return RedirectToAction(nameof(Profile));
        }

        private static decimal? ParseCoordinate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant)) return invariant;
            if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var localized)) return localized;
            return null;
        }

        [HttpGet]
        public IActionResult ForgotPassword()
        {
            return View();
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> ForgotPassword(string email)
        {
            email = (email ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email))
            {
                TempData["ToastError"] = "Indique su correo electronico.";
                return View();
            }

            var token = await _sqlStore.CreatePasswordResetTokenAsync(email);
            if (token is not null)
            {
                var resetUrl = Url.Action(nameof(ResetPassword), "Account", new { token }, Request.Scheme, Request.Host.Value)!;
                try
                {
                    await _emailService.SendAsync(email, email, "Restablecer contraseña", $"Recibimos una solicitud para cambiar su contraseña. Abra este enlace durante los próximos 30 minutos:\n\n{resetUrl}\n\nSi no realizó esta solicitud, ignore este correo.");
                }
                catch (InvalidOperationException ex)
                {
                    TempData["ToastError"] = ex.Message;
                    return RedirectToAction(nameof(ForgotPassword));
                }
            }
            TempData["ToastSuccess"] = "Si el correo está registrado, recibirá un enlace válido durante 30 minutos.";
            return RedirectToAction(nameof(Login));
        }

        [HttpGet]
        public IActionResult ResetPassword(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return RedirectToAction(nameof(ForgotPassword));
            ViewData["ResetToken"] = token;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(string token, string newPassword, string confirmPassword)
        {
            ViewData["ResetToken"] = token;
            if (string.IsNullOrWhiteSpace(token) || newPassword.Length < 8)
            {
                ViewData["ResetError"] = "La contraseña debe tener al menos 8 caracteres.";
                return View();
            }
            if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
            {
                ViewData["ResetError"] = "Las contraseñas no coinciden.";
                return View();
            }
            if (!await _sqlStore.ResetPasswordWithTokenAsync(token, newPassword))
            {
                ViewData["ResetError"] = "El enlace venció o ya fue utilizado. Solicite uno nuevo.";
                return View();
            }
            TempData["ToastSuccess"] = "Contraseña actualizada. Ya puede iniciar sesión.";
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

        private void DeleteLegacyAuthCookies(bool includeCurrent = true)
        {
            Response.Cookies.Delete("BakeSmartPatri.Auth");
            Response.Cookies.Delete("BakeSmartPatri.Auth.v2");
            Response.Cookies.Delete("BakeSmartPatri.Auth.v3");
            if (includeCurrent)
                Response.Cookies.Delete("BakeSmartPatri.Auth.v4");
            Response.Cookies.Delete(".AspNetCore.Antiforgery.gl4x9LQyqcE");
            Response.Cookies.Delete("BakeSmartPatri.Antiforgery.v2");
            Response.Cookies.Delete("BakeSmartPatri.Antiforgery.v3");
        }
    }
}
