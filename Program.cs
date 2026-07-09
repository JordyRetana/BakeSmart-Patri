using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using System.Security.Claims;
using BakeSmartPatri.Data;

if (args.Contains("--check-databases", StringComparer.OrdinalIgnoreCase))
{
    Environment.ExitCode = await DatabaseMigrationRunner.CheckConnectionsAsync();
    return;
}

if (args.Contains("--migrate-database", StringComparer.OrdinalIgnoreCase))
{
    Environment.ExitCode = await DatabaseMigrationRunner.MigrateAsync();
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration
    .AddJsonFile("appsettings.Azure.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var dataProtection = builder.Services
    .AddDataProtection()
    .SetApplicationName("BakeSmartPatri");

var dataProtectionConnectionString = builder.Configuration.GetConnectionString("BakeSmartDb");
if (builder.Configuration.GetValue<bool>("Features:UseSqlDatabase") &&
    !string.IsNullOrWhiteSpace(dataProtectionConnectionString))
{
    dataProtection.AddKeyManagementOptions(options =>
    {
        options.XmlRepository = new SqlDataProtectionKeyRepository(dataProtectionConnectionString);
    });
}
else
{
    var dataProtectionPath = Path.Combine(builder.Environment.ContentRootPath, ".data-protection");
    Directory.CreateDirectory(dataProtectionPath);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
}

builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
builder.Services.AddScoped<SqlStore>();
builder.Services.AddHttpClient("Nominatim", client =>
{
    client.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BakeSmartPatri/1.0 (contact@bakesmart.com)");
});


builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "BakeSmartPatri.Auth";
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/Denied";
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromHours(12);

        
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    });


builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", p => p.RequireRole("Admin"));
    options.AddPolicy("StaffOrAdmin", p => p.RequireRole("Staff", "Admin", "Cajero", "Repostero", "Supervisor"));
    options.AddPolicy("AnyUser", p => p.RequireRole("Admin", "Staff", "Cliente", "Cajero", "Repostero", "Supervisor"));

    
    options.AddPolicy("ClientOnly", p => p.RequireRole("Cliente"));
});


builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
});

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseResponseCompression();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "public,max-age=86400";
    }
});

app.UseRouting();

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    var isStaticAsset = Path.HasExtension(path);
    if (!isStaticAsset)
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
    }

    await next();
});

app.UseAuthentication();
app.UseAuthorization();


app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
