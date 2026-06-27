using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Claims;
using BakeSmartPatri.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var dataProtectionPath = Path.Combine(builder.Environment.ContentRootPath, ".data-protection");
Directory.CreateDirectory(dataProtectionPath);
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("BakeSmartPatri");

builder.Services.AddControllersWithViews();
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
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();


app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
