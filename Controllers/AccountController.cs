using BakeSmartPatri.Data;
using BakeSmartPatri.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace BakeSmartPatri.Controllers
{
    public class AccountController : Controller
    {
        private readonly SqlStore _sqlStore;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;

        public AccountController(SqlStore sqlStore, IEmailService emailService, IConfiguration configuration)
        {
            _sqlStore = sqlStore;
            _emailService = emailService;
            _configuration = configuration;
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
            ViewData["GoogleEnabled"] = IsGoogleEnabled;
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
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> Login(string email, string password, string? returnUrl = null)
        {
            email = (email ?? "").Trim().ToLowerInvariant();
            password ??= "";

            var result = await _sqlStore.AuthenticateSecureAsync(email, password);
            if (result.Status == SqlStore.SecureAuthStatus.Locked)
            {
                TempData["Toast"] = "Cuenta bloqueada temporalmente por varios intentos. Intente nuevamente en 15 minutos.";
                ViewData["ReturnUrl"] = returnUrl ?? "";
                ViewData["GoogleEnabled"] = IsGoogleEnabled;
                return View();
            }
            if (result.Status == SqlStore.SecureAuthStatus.EmailNotConfirmed)
            {
                TempData["Toast"] = "Confirme su correo antes de iniciar sesión.";
                ViewData["ReturnUrl"] = returnUrl ?? "";
                ViewData["GoogleEnabled"] = IsGoogleEnabled;
                return View();
            }
            if (result.User is null)
            {
                TempData["Toast"] = "Credenciales invalidas.";
                ViewData["ReturnUrl"] = returnUrl ?? "";
                ViewData["GoogleEnabled"] = IsGoogleEnabled;
                return View();
            }

            var user = result.User;
            if (result.Status == SqlStore.SecureAuthStatus.RequiresTwoFactor)
            {
                TempData["PendingTwoFactorEmail"] = user.Email;
                TempData["PendingTwoFactorRole"] = user.Role;
                TempData["PendingTwoFactorName"] = user.DisplayName;
                TempData["PendingTwoFactorReturnUrl"] = returnUrl ?? "";
                return RedirectToAction(nameof(TwoFactor));
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
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> Register(string firstName, string lastName, string email, string? phone, string? addressLine, string password, string confirmPassword, string? returnUrl = null)
        {
            firstName = (firstName ?? "").Trim();
            lastName = (lastName ?? "").Trim();
            email = (email ?? "").Trim().ToLowerInvariant();
            phone = string.IsNullOrWhiteSpace(phone) ? null : Regex.Replace(phone, @"[^\d+]", "");
            password ??= "";
            confirmPassword ??= "";

            if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName) || string.IsNullOrWhiteSpace(email))
            {
                TempData["Toast"] = "Completa nombre, apellidos y correo.";
                ViewData["ReturnUrl"] = returnUrl ?? "";
                return View();
            }

            if (!string.IsNullOrWhiteSpace(phone) && !Regex.IsMatch(phone, @"^\+[1-9]\d{7,14}$"))
            {
                TempData["Toast"] = "El teléfono debe incluir un prefijo de país y una cantidad válida de dígitos.";
                ViewData["ReturnUrl"] = returnUrl ?? "";
                return View();
            }

            if (!IsStrongPassword(password))
            {
                TempData["Toast"] = "Use al menos 12 caracteres con mayúscula, minúscula, número y símbolo.";
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
                await _sqlStore.MarkEmailUnconfirmedAsync(email);
            }
            catch (Exception ex)
            {
                TempData["Toast"] = ex.Message.Contains("Ya existe", StringComparison.OrdinalIgnoreCase)
                    ? "Ya existe un usuario con ese correo."
                    : "No se pudo completar el registro.";
                ViewData["ReturnUrl"] = returnUrl ?? "";
                return View();
            }

            var token = await _sqlStore.CreateEmailConfirmationTokenAsync(email);
            var confirmationUrl = Url.Action(nameof(ConfirmEmail), "Account", new { token }, Request.Scheme, Request.Host.Value)!;
            try
            {
                await _emailService.SendAsync(email, $"{firstName} {lastName}".Trim(), "Confirma tu cuenta BakeSmart Patri", $"Confirma tu correo durante las próximas 24 horas:\n\n{confirmationUrl}\n\nSi no creaste esta cuenta, ignora el mensaje.");
            }
            catch { TempData["Toast"] = "Cuenta creada, pero no se pudo enviar la confirmación. Contacte al administrador."; return RedirectToAction(nameof(Login)); }
            TempData["ToastSuccess"] = "Cuenta creada. Revise su correo para confirmarla antes de ingresar.";
            return RedirectToAction(nameof(Login));
        }

        [HttpGet]
        public async Task<IActionResult> ConfirmEmail(string? token)
        {
            if (string.IsNullOrWhiteSpace(token) || !await _sqlStore.ConfirmEmailAsync(token))
            {
                TempData["Toast"] = "El enlace de confirmación venció o ya fue utilizado.";
                return RedirectToAction(nameof(Login));
            }
            TempData["ToastSuccess"] = "Correo confirmado. Ya puede iniciar sesión.";
            return RedirectToAction(nameof(Login));
        }

        [HttpGet]
        public IActionResult TwoFactor()
        {
            if (TempData.Peek("PendingTwoFactorEmail") is null) return RedirectToAction(nameof(Login));
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> TwoFactor(string code)
        {
            var email = TempData.Peek("PendingTwoFactorEmail")?.ToString();
            if (string.IsNullOrWhiteSpace(email)) return RedirectToAction(nameof(Login));
            if (!await _sqlStore.VerifyTwoFactorAsync(email, code))
            {
                ViewData["Error"] = "El código no es válido o ya venció.";
                return View();
            }
            var user = new SqlStore.AuthUser(email, TempData["PendingTwoFactorRole"]?.ToString() ?? "Cliente", TempData["PendingTwoFactorName"]?.ToString() ?? email);
            var returnUrl = TempData["PendingTwoFactorReturnUrl"]?.ToString();
            await SignInUserAsync(user);
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
            return user.Role == "Cliente" ? RedirectToAction("Index", "Client") : RedirectToAction("Index", "Dashboard");
        }

        [Authorize]
        [HttpGet]
        public async Task<IActionResult> Security()
        {
            var email = User.FindFirstValue(ClaimTypes.Email) ?? "";
            var state = await _sqlStore.GetUserSecurityAsync(email);
            ViewData["TwoFactorEnabled"] = state.TwoFactorEnabled;
            if (!state.TwoFactorEnabled)
            {
                var secret = string.IsNullOrWhiteSpace(state.TotpSecret) ? await _sqlStore.BeginTwoFactorSetupAsync(email) : state.TotpSecret;
                ViewData["Secret"] = secret;
                ViewData["OtpAuthUri"] = $"otpauth://totp/BakeSmart%20Patri:{Uri.EscapeDataString(email)}?secret={secret}&issuer=BakeSmart%20Patri&digits=6&period=30";
            }
            return View();
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EnableTwoFactor(string code)
        {
            var email = User.FindFirstValue(ClaimTypes.Email) ?? "";
            if (!await _sqlStore.EnableTwoFactorAsync(email, code)) TempData["ToastError"] = "Código incorrecto. Verifique la hora del teléfono e intente nuevamente.";
            else
            {
                await SignInUserAsync(new SqlStore.AuthUser(email, User.FindFirstValue(ClaimTypes.Role) ?? "Cliente", User.FindFirstValue(ClaimTypes.Name) ?? email));
                TempData["ToastSuccess"] = "Autenticación de dos pasos activada correctamente.";
            }
            return RedirectToAction(nameof(Security));
        }

        [HttpGet]
        public IActionResult GoogleLogin(string? returnUrl = null)
        {
            if (!IsGoogleEnabled) { TempData["Toast"] = "El acceso con Google aún no está configurado."; return RedirectToAction(nameof(Login)); }
            var callback = Url.Action(nameof(GoogleCallback), "Account", new { returnUrl });
            return Challenge(new AuthenticationProperties { RedirectUri = callback }, GoogleDefaults.AuthenticationScheme);
        }

        [HttpGet]
        public async Task<IActionResult> GoogleCallback(string? returnUrl = null)
        {
            var external = await HttpContext.AuthenticateAsync("External");
            if (!external.Succeeded || external.Principal is null) { TempData["Toast"] = "No se pudo validar la cuenta de Google."; return RedirectToAction(nameof(Login)); }
            var email = external.Principal.FindFirstValue(ClaimTypes.Email);
            var providerId = external.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
            var name = external.Principal.FindFirstValue(ClaimTypes.Name) ?? email;
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(providerId)) { TempData["Toast"] = "Google no proporcionó un correo válido."; return RedirectToAction(nameof(Login)); }
            var user = await _sqlStore.RegisterOrGetGoogleUserAsync(email, name ?? email, providerId);
            await HttpContext.SignOutAsync("External");
            await SignInUserAsync(user);
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
            return user.Role == "Cliente" ? RedirectToAction("Index", "Client") : RedirectToAction("Index", "Dashboard");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
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
                if (!IsStrongPassword(newPassword))
                {
                    TempData["ToastError"] = "Use al menos 12 caracteres con mayúscula, minúscula, número y símbolo.";
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
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("auth")]
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
                    ViewData["ResetError"] = ex.Message;
                    return View();
                }
            }
            ViewData["ResetMessage"] = "Si el correo está registrado, recibirá un enlace válido durante 30 minutos. Revise también Spam y Promociones.";
            return View();
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
            if (string.IsNullOrWhiteSpace(token) || !IsStrongPassword(newPassword))
            {
                ViewData["ResetError"] = "Use al menos 12 caracteres con mayúscula, minúscula, número y símbolo.";
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

        private bool IsGoogleEnabled => !string.IsNullOrWhiteSpace(_configuration["Authentication:Google:ClientId"]) && !string.IsNullOrWhiteSpace(_configuration["Authentication:Google:ClientSecret"]);

        private static bool IsStrongPassword(string value) =>
            value.Length >= 12 && value.Any(char.IsUpper) && value.Any(char.IsLower) && value.Any(char.IsDigit) && value.Any(character => !char.IsLetterOrDigit(character));

        private async Task SignInUserAsync(SqlStore.AuthUser user)
        {
            var security = await _sqlStore.GetUserSecurityAsync(user.Email);
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Email),
                new(ClaimTypes.Name, user.DisplayName),
                new(ClaimTypes.Email, user.Email),
                new(ClaimTypes.Role, user.Role),
                new("bakesmart:2fa", security.TwoFactorEnabled ? "enabled" : "disabled"),
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
