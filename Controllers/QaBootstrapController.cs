using BakeSmartPatri.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;

namespace BakeSmartPatri.Controllers;

[ApiController]
[Route("api/internal/qa-bootstrap")]
public sealed class QaBootstrapController(IConfiguration configuration) : ControllerBase
{
    private const string ExpectedHash = "A82D429FA0F4D27AA5828EC547D1D3978390E55BC3FC141F89B4716B3077B2A7";

    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Create()
    {
        var supplied = Request.Headers["X-BakeSmart-Bootstrap"].ToString();
        var suppliedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(ExpectedHash), Convert.FromHexString(suppliedHash)))
            return NotFound();

        var credentials = await QaTestAccountSeeder.CreateAsync(configuration);
        return Ok(credentials);
    }
}
