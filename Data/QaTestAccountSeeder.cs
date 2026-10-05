using System.Security.Cryptography;

namespace BakeSmartPatri.Data;

public static class QaTestAccountSeeder
{
    public sealed record Credential(string Role, string Email, string Password);
    private static readonly (string Role, string Slug, string FirstName)[] Profiles =
    {
        ("Admin", "admin", "Administrador"),
        ("Staff", "staff", "Staff"),
        ("Cajero", "cajero", "Cajero"),
        ("Supervisor", "supervisor", "Supervisor"),
        ("Repostero", "repostero", "Repostero"),
        ("EncargadoRecetas", "recetas", "Encargado de Recetas"),
        ("Cliente", "cliente", "Cliente")
    };

    public static async Task<int> RunAsync(IConfiguration configuration, string outputPath)
    {
        var credentials = await CreateAsync(configuration);

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllLinesAsync(fullPath, Format(credentials));
        return 0;
    }

    public static async Task<IReadOnlyList<Credential>> CreateAsync(IConfiguration configuration)
    {
        var store = new SqlStore(configuration);
        var existing = await store.UsersAsync();
        var credentials = new List<Credential>();

        foreach (var profile in Profiles)
        {
            var email = $"qa.{profile.Slug}@bakesmart.test";
            var password = CreatePassword();
            var existingUser = existing.FirstOrDefault(row => string.Equals(
                row.GetType().GetProperty("email")?.GetValue(row)?.ToString(), email,
                StringComparison.OrdinalIgnoreCase));
            var idValue = existingUser?.GetType().GetProperty("id")?.GetValue(existingUser);
            var id = idValue is null ? (int?)null : Convert.ToInt32(idValue);

            await store.SaveUserAsync(new SqlStore.UserInput(
                id,
                profile.FirstName,
                "Pruebas BakeSmart",
                email,
                "0000-0000",
                "Datos controlados para pruebas QA",
                profile.Role,
                password,
                true));
            await store.PrepareTestAccountAsync(email);
            credentials.Add(new Credential(profile.Role, email, password));
        }
        return credentials;
    }

    public static IEnumerable<string> Format(IReadOnlyList<Credential> credentials)
    {
        var lines = new List<string>
        {
            "BAKESMART PATRI - CUENTAS PROTEGIDAS PARA PRUEBAS",
            $"Generadas: {DateTimeOffset.Now:dd/MM/yyyy HH:mm zzz}",
            "",
            "Estas cuentas conservan los accesos de su rol, pero no pueden modificar usuarios,",
            "roles, seguridad, configuración crítica, pagos, contabilidad ni eliminar registros.",
            ""
        };
        foreach (var item in credentials)
        {
            lines.Add($"ROL: {item.Role}");
            lines.Add($"CORREO: {item.Email}");
            lines.Add($"CONTRASEÑA: {item.Password}");
            lines.Add(new string('-', 58));
        }
        return lines;
    }

    private static string CreatePassword()
    {
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string digits = "23456789";
        const string symbols = "!@$%*-_+";
        const string all = lower + upper + digits + symbols;
        var chars = new List<char>
        {
            Pick(lower), Pick(upper), Pick(digits), Pick(symbols)
        };
        while (chars.Count < 16) chars.Add(Pick(all));
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }

    private static char Pick(string source) => source[RandomNumberGenerator.GetInt32(source.Length)];
}
