using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BakeSmartPatri.Services;

public sealed class LoginAntiforgeryRecoveryFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is AntiforgeryValidationFailedResult &&
            string.Equals(context.RouteData.Values["controller"]?.ToString(), "Account", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(context.RouteData.Values["action"]?.ToString(), "Login", StringComparison.OrdinalIgnoreCase))
            context.Result = new RedirectToActionResult("Login", "Account", new { formExpired = true });
    }
    public void OnResultExecuted(ResultExecutedContext context) { }
}
