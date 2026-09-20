using System.Security.Claims;
using BakeSmartPatri.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Text.RegularExpressions;

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
        await sqlStore.AddAuditLogAsync("OPERACION_API", DescribeOperation(request.Method, path), email);
    }

    private static string DescribeOperation(string method, string path)
    {
        var production = Regex.Match(path, @"^/api/production/(\d+)/advance$", RegexOptions.IgnoreCase);
        if (production.Success) return $"El pedido #{production.Groups[1].Value} avanzó al siguiente estado de producción.";

        var delivery = Regex.Match(path, @"^/api/orders/(\d+)/advance-delivery$", RegexOptions.IgnoreCase);
        if (delivery.Success) return $"El pedido #{delivery.Groups[1].Value} avanzó en el proceso de entrega.";

        if (path.Contains("/payments/", StringComparison.OrdinalIgnoreCase)) return "Se actualizó el estado de un pago.";
        if (path.Contains("/settings", StringComparison.OrdinalIgnoreCase)) return "Se guardó la configuración del sistema.";
        if (path.Contains("/inventory", StringComparison.OrdinalIgnoreCase)) return "Se actualizó el inventario.";
        return $"Se ejecutó una operación del sistema ({method.ToUpperInvariant()}).";
    }
}
