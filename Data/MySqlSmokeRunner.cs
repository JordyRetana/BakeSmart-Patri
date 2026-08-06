using Microsoft.Extensions.Configuration;

namespace BakeSmartPatri.Data;

internal static class MySqlSmokeRunner
{
    public static async Task<int> RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("BAKESMART_MYSQL")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__BakeSmartDb");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("Missing BAKESMART_MYSQL or ConnectionStrings__BakeSmartDb.");
            return 1;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:BakeSmartDb"] = connectionString,
                ["Features:UseSqlDatabase"] = "true",
                ["Database:Provider"] = "MySql",
                ["Features:DisableSqlDataProtection"] = "true",
                ["Features:UseSqlDataProtection"] = "false"
            })
            .Build();

        var store = new SqlStore(configuration);
        var checks = new List<(string Name, Func<Task> Run)>
        {
            ("health", async () => _ = await store.HealthAsync()),
            ("login", async () =>
            {
                var user = await store.AuthenticateAsync("admin@demo.com", "12345678");
                if (user is null) throw new InvalidOperationException("Login demo no valido.");
            }),
            ("dashboard", async () => _ = await store.DashboardAsync()),
            ("catalog-products", async () => _ = await store.CatalogProductsAsync()),
            ("orders", async () => _ = await store.OrdersAsync()),
            ("customers", async () => _ = await store.CustomersAsync()),
            ("promotions", async () => _ = await store.PromotionsAsync()),
            ("promotion-discounts", () =>
            {
                if (SqlStore.NormalizePromotionDiscount(7.5m) != 0.075m ||
                    SqlStore.NormalizePromotionDiscount(10m) != 0.10m ||
                    SqlStore.NormalizePromotionDiscount(100m) != 1m)
                    throw new InvalidOperationException("La normalización de descuentos no conserva el porcentaje indicado.");
                return Task.CompletedTask;
            }),
            ("accounting", async () => _ = await store.AccountingOverviewAsync()),
            ("profile", async () => _ = await store.GetProfileAsync("cliente@demo.com")),
            ("pos-config", async () => _ = await store.PosConfigAsync()),
            ("cash-sessions", async () => _ = await store.CashSessionsAsync("cajero@demo.com")),
            ("recent-pos-sales", async () => _ = await store.RecentPosSalesAsync())
        };

        foreach (var check in checks)
        {
            try
            {
                await check.Run();
                Console.WriteLine($"OK {check.Name}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL {check.Name}: {ex.Message}");
                return 2;
            }
        }

        if (string.Equals(Environment.GetEnvironmentVariable("BAKESMART_WRITE_SMOKE"), "true", StringComparison.OrdinalIgnoreCase))
        {
            var cashId = await store.OpenCashSessionAsync(1000m, "cajero@demo.com");
            Console.WriteLine($"OK open-cash #{cashId}");

            var product = (await store.CatalogProductsAsync()).First(p => p.Stock >= 1);
            var orderId = await store.RegisterSaleAsync(new SqlStore.SaleInput(
                "Cliente Smoke",
                $"smoke-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}@local",
                "60000000",
                "Efectivo",
                product.UnitPrice,
                0,
                Math.Round(product.UnitPrice * 0.13m, 2),
                Math.Round(product.UnitPrice * 1.13m, 2),
                "Prueba tecnica Aiven",
                new[] { new SqlStore.SaleItemInput(product.Id, 1, product.UnitPrice) }),
                "cajero@demo.com");
            Console.WriteLine($"OK register-sale order=#{orderId}");

            await store.CloseCashSessionAsync(cashId, 1000m, "cajero@demo.com");
            Console.WriteLine($"OK close-cash #{cashId}");
        }

        return 0;
    }
}
