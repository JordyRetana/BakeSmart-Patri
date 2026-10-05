using System.Security.Claims;

namespace BakeSmartPatri.Services;

/// <summary>
/// Keeps QA accounts useful for end-to-end testing while preventing changes to
/// security, identities, system configuration and real payment/accounting data.
/// This check runs on the server, so hiding a button or calling the API directly
/// cannot bypass it.
/// </summary>
public sealed class TestAccountRestrictionsMiddleware(RequestDelegate next)
{
    public const string TestAccountClaim = "bakesmart:test-account";

    private static readonly string[] ProtectedMutationPrefixes =
    {
        "/api/users",
        "/api/roles",
        "/api/settings",
        "/api/pos/payment-methods",
        "/api/accounting",
        "/api/payments",
        "/api/assets/site-images",
        "/api/marketing/campaigns",
        "/admin/save"
    };

    public async Task InvokeAsync(HttpContext context, BakeSmartPatri.Data.SqlStore store)
    {
        if (await IsBlockedMutationAsync(context, store))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Esta acción está protegida para las cuentas de prueba. Use una cuenta administrativa real para cambiar seguridad, usuarios, configuración o datos financieros."
            });
            return;
        }

        await next(context);
    }

    internal static async Task<bool> IsBlockedMutationAsync(HttpContext context, BakeSmartPatri.Data.SqlStore store)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return false;

        var isTestAccount = string.Equals(context.User.FindFirstValue(TestAccountClaim), "true", StringComparison.OrdinalIgnoreCase);
        if (!isTestAccount)
        {
            var email = context.User.FindFirstValue(ClaimTypes.Email) ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            isTestAccount = !string.IsNullOrWhiteSpace(email) && await store.IsTestAccountAsync(email);
        }
        if (!isTestAccount) return false;

        var method = context.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
            return false;

        // QA users may exercise normal operational flows, but never permanent deletion.
        if (HttpMethods.IsDelete(method))
            return true;

        var path = context.Request.Path.Value ?? string.Empty;
        return ProtectedMutationPrefixes.Any(prefix =>
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
