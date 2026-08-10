using System.Security.Claims;
using BakeSmartPatri.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BakeSmartPatri.Services;

/// <summary>Records successful state-changing API calls without storing request payloads.</summary>
public sealed class AuditMutationFilter(SqlStore sqlStore) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executed = await next();
        var request = context.HttpContext.Request;
        if (!HttpMethods.IsPost(request.Method) && !HttpMethods.IsPut(request.Method) &&
            !HttpMethods.IsPatch(request.Method) && !HttpMethods.IsDelete(request.Method)) return;

        if (executed.Exception is not null || context.HttpContext.Response.StatusCode >= StatusCodes.Status400BadRequest) return;
        var email = context.HttpContext.User.FindFirstValue(ClaimTypes.Email) ??
                    context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(email)) return;

        var path = request.Path.Value ?? "/api";
        await sqlStore.AddAuditLogAsync("OPERACION_API", $"{request.Method} {path}", email);
    }
}
