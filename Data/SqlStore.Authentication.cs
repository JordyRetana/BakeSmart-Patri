using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;

namespace BakeSmartPatri.Data;

public sealed partial class SqlStore
{
    private static readonly SemaphoreSlim AuthenticationSchemaLock = new(1, 1);
    private static volatile bool _authenticationSchemaReady;

    public async Task<SecureAuthResult> AuthenticateSecureAsync(string email, string password)
    {
        await EnsureAuthenticationTablesAsync();
        email = email.Trim().ToLowerInvariant();
        var table = UseMySql ? "SeguridadUsuarios" : "dbo.SeguridadUsuarios";
        var now = UseMySql ? "UTC_TIMESTAMP()" : "SYSUTCDATETIME()";
        var state = (await QueryAsync($"SELECT FailedLoginAttempts, LockoutEnd FROM {table} WHERE LOWER(Email)=LOWER(@Email);", reader => new
        {
            Attempts = reader.GetInt32("FailedLoginAttempts"),
            LockoutEnd = reader.GetNullableDateTime("LockoutEnd")
        }, new SqlParameter("@Email", email))).FirstOrDefault();

        if (state?.LockoutEnd is DateTime lockout && lockout > DateTime.UtcNow)
            return new(null, SecureAuthStatus.Locked, lockout, null);

        var user = await AuthenticateAsync(email, password);
        if (user is null)
        {
            var attempts = (state?.Attempts ?? 0) + 1;
            var lockExpression = attempts >= 5
                ? (UseMySql ? "DATE_ADD(UTC_TIMESTAMP(), INTERVAL 15 MINUTE)" : "DATEADD(minute, 15, SYSUTCDATETIME())")
                : "NULL";
            await ExecuteAsync($"UPDATE {table} SET FailedLoginAttempts=@Attempts, LockoutEnd={lockExpression}, UpdatedAt={now} WHERE LOWER(Email)=LOWER(@Email);",
                new SqlParameter("@Attempts", attempts), new SqlParameter("@Email", email));
            return new(null, attempts >= 5 ? SecureAuthStatus.Locked : SecureAuthStatus.Invalid, attempts >= 5 ? DateTime.UtcNow.AddMinutes(15) : null, null);
        }

        var security = await GetUserSecurityAsync(email);
        await ExecuteAsync($"UPDATE {table} SET FailedLoginAttempts=0, LockoutEnd=NULL, UpdatedAt={now} WHERE LOWER(Email)=LOWER(@Email);", new SqlParameter("@Email", email));
        var isTestAccount = Convert.ToInt32(await ScalarAsync($"SELECT IsTestAccount FROM {table} WHERE LOWER(Email)=LOWER(@Email);", new SqlParameter("@Email", email)) ?? 0) == 1;
        if (!security.EmailConfirmed && !isTestAccount)
            return new(user, SecureAuthStatus.EmailNotConfirmed, null, security);
        if (security.TwoFactorEnabled)
            return new(user, SecureAuthStatus.RequiresTwoFactor, null, security);
        return new(user, SecureAuthStatus.Success, null, security);
    }

    public async Task<UserSecurityState> GetUserSecurityAsync(string email)
    {
        await EnsureAuthenticationTablesAsync();
        var table = UseMySql ? "SeguridadUsuarios" : "dbo.SeguridadUsuarios";
        var rows = await QueryAsync($"SELECT EmailConfirmed, TwoFactorEnabled, TotpSecret, PasswordSetupRequired FROM {table} WHERE LOWER(Email)=LOWER(@Email);", reader => new UserSecurityState(
            reader.GetBoolean("EmailConfirmed"), reader.GetBoolean("TwoFactorEnabled"), reader.GetNullableString("TotpSecret"), reader.GetBoolean("PasswordSetupRequired")), new SqlParameter("@Email", email));
        return rows.FirstOrDefault() ?? new UserSecurityState(false, false, null, false);
    }

    public async Task SetTestAccountAsync(string email, bool enabled)
    {
        await EnsureAuthenticationTablesAsync();
        var table = UseMySql ? "SeguridadUsuarios" : "dbo.SeguridadUsuarios";
        await ExecuteAsync($"UPDATE {table} SET IsTestAccount=@Enabled WHERE LOWER(Email)=LOWER(@Email);",
            new SqlParameter("@Enabled", enabled), new SqlParameter("@Email", email.Trim().ToLowerInvariant()));
    }

    public async Task<bool> NeedsEmailConfirmationAsync(string email)
    {
        await EnsureAuthenticationTablesAsync();
        var users = UseMySql ? "Usuarios" : "dbo.Usuarios";
        var security = UseMySql ? "SeguridadUsuarios" : "dbo.SeguridadUsuarios";
        var emailJoin = UseMySql ? "LOWER(s.Email) COLLATE utf8mb4_unicode_ci=LOWER(u.Email) COLLATE utf8mb4_unicode_ci" : "LOWER(s.Email)=LOWER(u.Email)";
        var count = await ScalarAsync($"SELECT COUNT(1) FROM {users} u INNER JOIN {security} s ON {emailJoin} WHERE LOWER(u.Email)=LOWER(@Email) AND u.IsActive=1 AND s.EmailConfirmed=0;",
            new SqlParameter("@Email", email.Trim().ToLowerInvariant()));
        return Convert.ToInt32(count ?? 0) > 0;
    }

    public async Task<string> CreateEmailConfirmationTokenAsync(string email)
    {
        await EnsureAuthenticationTablesAsync();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var table = UseMySql ? "TokensConfirmacionCorreo" : "dbo.TokensConfirmacionCorreo";
        var now = UseMySql ? "UTC_TIMESTAMP()" : "SYSUTCDATETIME()";
        var expiry = UseMySql ? "DATE_ADD(UTC_TIMESTAMP(), INTERVAL 24 HOUR)" : "DATEADD(hour, 24, SYSUTCDATETIME())";
        await ExecuteAsync($"UPDATE {table} SET UsedAt={now} WHERE LOWER(Email)=LOWER(@Email) AND UsedAt IS NULL; INSERT INTO {table}(Email,TokenHash,ExpiresAt,CreatedAt) VALUES(@Email,@Hash,{expiry},{now});",
            new SqlParameter("@Email", email), new SqlParameter("@Hash", hash));
        return token;
    }

    public async Task MarkEmailUnconfirmedAsync(string email)
    {
        await EnsureAuthenticationTablesAsync();
        var table = UseMySql ? "SeguridadUsuarios" : "dbo.SeguridadUsuarios";
        var now = UseMySql ? "UTC_TIMESTAMP()" : "SYSUTCDATETIME()";
        var sql = UseMySql
            ? $"INSERT INTO {table}(Email,EmailConfirmed,UpdatedAt) VALUES(LOWER(@Email),0,{now}) ON DUPLICATE KEY UPDATE EmailConfirmed=0,UpdatedAt={now};"
            : $"IF EXISTS(SELECT 1 FROM {table} WHERE LOWER(Email)=LOWER(@Email)) UPDATE {table} SET EmailConfirmed=0,UpdatedAt={now} WHERE LOWER(Email)=LOWER(@Email); ELSE INSERT INTO {table}(Email,EmailConfirmed,UpdatedAt) VALUES(LOWER(@Email),0,{now});";
        await ExecuteAsync(sql, new SqlParameter("@Email", email.Trim().ToLowerInvariant()));
    }

    public async Task<bool> ConfirmEmailAsync(string token)
    {
        await EnsureAuthenticationTablesAsync();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var tokens = UseMySql ? "TokensConfirmacionCorreo" : "dbo.TokensConfirmacionCorreo";
        var security = UseMySql ? "SeguridadUsuarios" : "dbo.SeguridadUsuarios";
        var now = UseMySql ? "UTC_TIMESTAMP()" : "SYSUTCDATETIME()";
        var email = (await QueryAsync($"SELECT Email FROM {tokens} WHERE TokenHash=@Hash AND UsedAt IS NULL AND ExpiresAt>{now};", r => r.GetString("Email"), new SqlParameter("@Hash", hash))).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(email)) return false;
        var confirmSql = UseMySql
            ? $"INSERT INTO {security}(Email,EmailConfirmed,UpdatedAt) VALUES(LOWER(@Email),1,{now}) ON DUPLICATE KEY UPDATE EmailConfirmed=1,UpdatedAt={now}; UPDATE {tokens} SET UsedAt={now} WHERE TokenHash=@Hash;"
            : $"IF EXISTS(SELECT 1 FROM {security} WHERE LOWER(Email)=LOWER(@Email)) UPDATE {security} SET EmailConfirmed=1,UpdatedAt={now} WHERE LOWER(Email)=LOWER(@Email); ELSE INSERT INTO {security}(Email,EmailConfirmed,UpdatedAt) VALUES(LOWER(@Email),1,{now}); UPDATE {tokens} SET UsedAt={now} WHERE TokenHash=@Hash;";
        await ExecuteAsync(confirmSql, new SqlParameter("@Email", email), new SqlParameter("@Hash", hash));
        return (await GetUserSecurityAsync(email)).EmailConfirmed;
    }

    public async Task<string> BeginTwoFactorSetupAsync(string email)
    {
        await EnsureAuthenticationTablesAsync();
        var secret = Base32Encode(RandomNumberGenerator.GetBytes(20));
        var table = UseMySql ? "SeguridadUsuarios" : "dbo.SeguridadUsuarios";
        var now = UseMySql ? "UTC_TIMESTAMP()" : "SYSUTCDATETIME()";
        await ExecuteAsync($"UPDATE {table} SET TotpSecret=@Secret,TwoFactorEnabled=0,UpdatedAt={now} WHERE LOWER(Email)=LOWER(@Email);", new SqlParameter("@Secret", secret), new SqlParameter("@Email", email));
        return secret;
    }

    public async Task<bool> EnableTwoFactorAsync(string email, string code)
    {
        var state = await GetUserSecurityAsync(email);
        if (string.IsNullOrWhiteSpace(state.TotpSecret) || !VerifyTotp(state.TotpSecret, code)) return false;
        var table = UseMySql ? "SeguridadUsuarios" : "dbo.SeguridadUsuarios";
        await ExecuteAsync($"UPDATE {table} SET TwoFactorEnabled=1 WHERE LOWER(Email)=LOWER(@Email);", new SqlParameter("@Email", email));
        return true;
    }

    public async Task<bool> VerifyTwoFactorAsync(string email, string code)
    {
        var state = await GetUserSecurityAsync(email);
        return state.TwoFactorEnabled && !string.IsNullOrWhiteSpace(state.TotpSecret) && VerifyTotp(state.TotpSecret, code);
    }

    public async Task<int> GetSessionVersionAsync(string email)
    {
        try
        {
            await EnsureAuthenticationTablesAsync();
            var table = UseMySql ? "SeguridadUsuarios" : "dbo.SeguridadUsuarios";
            return Convert.ToInt32(await ScalarAsync($"SELECT SessionVersion FROM {table} WHERE LOWER(Email)=LOWER(@Email);", new SqlParameter("@Email", email)) ?? 0);
        }
        catch
        {
            // Session invalidation is an additional protection. A pending
            // schema migration must never prevent a valid user from signing in.
            return 0;
        }
    }

    public async Task<bool> RequestTwoFactorResetAsync(string email)
    {
        await EnsureAuthenticationTablesAsync();
        email = email.Trim().ToLowerInvariant();
        var users = UseMySql ? "Usuarios" : "dbo.Usuarios";
        var security = UseMySql ? "SeguridadUsuarios" : "dbo.SeguridadUsuarios";
        var requests = UseMySql ? "SolicitudesRestablecimiento2FA" : "dbo.SolicitudesRestablecimiento2FA";
        var emailJoin = UseMySql ? "LOWER(s.Email) COLLATE utf8mb4_unicode_ci=LOWER(u.Email) COLLATE utf8mb4_unicode_ci" : "LOWER(s.Email)=LOWER(u.Email)";
        var exists = Convert.ToInt32(await ScalarAsync($"SELECT COUNT(1) FROM {users} u INNER JOIN {security} s ON {emailJoin} WHERE LOWER(u.Email)=LOWER(@Email) AND u.IsActive=1 AND s.TwoFactorEnabled=1;", new SqlParameter("@Email", email)) ?? 0) > 0;
        if (!exists) return false;
        var sql = UseMySql
            ? $"INSERT INTO {requests}(Email,Status,RequestedAt) VALUES(@Email,'PENDIENTE',UTC_TIMESTAMP()) ON DUPLICATE KEY UPDATE Status='PENDIENTE',RequestedAt=UTC_TIMESTAMP(),ResolvedAt=NULL,ResolvedBy=NULL;"
            : $"IF EXISTS(SELECT 1 FROM {requests} WHERE LOWER(Email)=LOWER(@Email)) UPDATE {requests} SET Status='PENDIENTE',RequestedAt=SYSUTCDATETIME(),ResolvedAt=NULL,ResolvedBy=NULL WHERE LOWER(Email)=LOWER(@Email); ELSE INSERT INTO {requests}(Email,Status,RequestedAt) VALUES(@Email,'PENDIENTE',SYSUTCDATETIME());";
        await ExecuteAsync(sql, new SqlParameter("@Email", email));
        return true;
    }

    public async Task<string?> ResetTwoFactorAsync(int userId, string resolvedBy)
    {
        await EnsureAuthenticationTablesAsync();
        var users = UseMySql ? "Usuarios" : "dbo.Usuarios";
        var security = UseMySql ? "SeguridadUsuarios" : "dbo.SeguridadUsuarios";
        var requests = UseMySql ? "SolicitudesRestablecimiento2FA" : "dbo.SolicitudesRestablecimiento2FA";
        var email = (await QueryAsync($"SELECT Email FROM {users} WHERE UserId=@UserId;", r => r.GetString("Email"), new SqlParameter("@UserId", userId))).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(email)) return null;
        var now = UseMySql ? "UTC_TIMESTAMP()" : "SYSUTCDATETIME()";
        await ExecuteAsync($"UPDATE {security} SET TwoFactorEnabled=0,TotpSecret=NULL,FailedLoginAttempts=0,LockoutEnd=NULL,SessionVersion=SessionVersion+1,UpdatedAt={now} WHERE LOWER(Email)=LOWER(@Email); UPDATE {requests} SET Status='RESUELTA',ResolvedAt={now},ResolvedBy=@ResolvedBy WHERE LOWER(Email)=LOWER(@Email) AND Status='PENDIENTE';",
            new SqlParameter("@Email", email), new SqlParameter("@ResolvedBy", resolvedBy));
        return email;
    }

    public async Task<AuthUser> RegisterOrGetGoogleUserAsync(string email, string displayName, string providerId)
    {
        await EnsureAuthenticationTablesAsync();
        var existing = await FindAuthUserAsync(email);
        var isNewGoogleAccount = existing is null;
        if (isNewGoogleAccount)
        {
            var parts = displayName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            await RegisterCustomerAsync(new RegisterCustomerInput(parts.ElementAtOrDefault(0) ?? "Cliente", parts.ElementAtOrDefault(1) ?? "Google", email, null, null, Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))));
            // Registration creates the user first; this idempotent pass creates
            // its security row before the external identity is linked.
            await EnsureAuthenticationTablesAsync();
            existing = await FindAuthUserAsync(email) ?? throw new InvalidOperationException("No se pudo crear la cuenta de Google.");
        }
        var table = UseMySql ? "SeguridadUsuarios" : "dbo.SeguridadUsuarios";
        await ExecuteAsync($"UPDATE {table} SET EmailConfirmed=1,ExternalProvider='Google',ExternalProviderId=@ProviderId,PasswordSetupRequired=CASE WHEN @IsNew=1 THEN 1 ELSE PasswordSetupRequired END,PasswordSetupMigrated=1 WHERE LOWER(Email)=LOWER(@Email);", new SqlParameter("@ProviderId", providerId), new SqlParameter("@IsNew", isNewGoogleAccount ? 1 : 0), new SqlParameter("@Email", email));
        return existing!;
    }

    public async Task SetExternalAccountPasswordAsync(string email, string password)
    {
        await EnsureAuthenticationTablesAsync();
        var users = UseMySql ? "Usuarios" : "dbo.Usuarios";
        var security = UseMySql ? "SeguridadUsuarios" : "dbo.SeguridadUsuarios";
        var now = UseMySql ? "UTC_TIMESTAMP()" : "SYSUTCDATETIME()";
        await ExecuteAsync($"UPDATE {users} SET PasswordHash=@PasswordHash WHERE LOWER(Email)=LOWER(@Email) AND IsActive=1; UPDATE {security} SET PasswordSetupRequired=0,PasswordSetupMigrated=1,UpdatedAt={now} WHERE LOWER(Email)=LOWER(@Email);",
            new SqlParameter("@PasswordHash", HashPassword(password)), new SqlParameter("@Email", email));
    }

    private async Task<AuthUser?> FindAuthUserAsync(string email)
    {
        var sql = UseMySql
            ? "SELECT u.Email,u.FirstName,u.LastName,r.RoleName FROM Usuarios u JOIN Roles r ON r.RoleId=u.RoleId WHERE LOWER(u.Email)=LOWER(@Email) AND u.IsActive=1 LIMIT 1;"
            : "SELECT TOP 1 u.Email,u.FirstName,u.LastName,r.RoleName FROM dbo.Usuarios u JOIN dbo.Roles r ON r.RoleId=u.RoleId WHERE LOWER(u.Email)=LOWER(@Email) AND u.IsActive=1;";
        return (await QueryAsync(sql, r => new AuthUser(r.GetString("Email"), r.GetString("RoleName"), $"{r.GetString("FirstName")} {r.GetString("LastName")}".Trim()), new SqlParameter("@Email", email))).FirstOrDefault();
    }

    private async Task EnsureAuthenticationTablesAsync()
    {
        if (_authenticationSchemaReady) return;

        await AuthenticationSchemaLock.WaitAsync();
        try
        {
            if (_authenticationSchemaReady) return;

            if (UseMySql)
            {
                await ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS SeguridadUsuarios (Email varchar(254) NOT NULL PRIMARY KEY, EmailConfirmed bit NOT NULL DEFAULT 0, TwoFactorEnabled bit NOT NULL DEFAULT 0, TotpSecret varchar(128) NULL, FailedLoginAttempts int NOT NULL DEFAULT 0, LockoutEnd datetime NULL, ExternalProvider varchar(40) NULL, ExternalProviderId varchar(255) NULL, UpdatedAt datetime NOT NULL DEFAULT CURRENT_TIMESTAMP) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
                """);
                var hasPasswordSetupRequired = Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='SeguridadUsuarios' AND COLUMN_NAME='PasswordSetupRequired';")) > 0;
                if (!hasPasswordSetupRequired)
                    await ExecuteAsync("ALTER TABLE SeguridadUsuarios ADD COLUMN PasswordSetupRequired bit NOT NULL DEFAULT 0;");
                var hasPasswordSetupMigrated = Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='SeguridadUsuarios' AND COLUMN_NAME='PasswordSetupMigrated';")) > 0;
                if (!hasPasswordSetupMigrated)
                    await ExecuteAsync("ALTER TABLE SeguridadUsuarios ADD COLUMN PasswordSetupMigrated bit NOT NULL DEFAULT 0;");
                try
                {
                    if (Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='SeguridadUsuarios' AND COLUMN_NAME='SessionVersion';")) == 0)
                        await ExecuteAsync("ALTER TABLE SeguridadUsuarios ADD COLUMN SessionVersion int NOT NULL DEFAULT 0;");
                }
                catch { }
                await ExecuteAsync("""
                INSERT IGNORE INTO SeguridadUsuarios(Email,EmailConfirmed,UpdatedAt) SELECT LOWER(Email),1,UTC_TIMESTAMP() FROM Usuarios;
                UPDATE SeguridadUsuarios SET PasswordSetupRequired=CASE WHEN ExternalProvider='Google' THEN 1 ELSE 0 END,PasswordSetupMigrated=1 WHERE PasswordSetupMigrated=0;
                CREATE TABLE IF NOT EXISTS TokensConfirmacionCorreo (TokenId int NOT NULL AUTO_INCREMENT PRIMARY KEY, Email varchar(254) NOT NULL, TokenHash char(64) NOT NULL UNIQUE, ExpiresAt datetime NOT NULL, UsedAt datetime NULL, CreatedAt datetime NOT NULL) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
                """);
                try { await ExecuteAsync("CREATE TABLE IF NOT EXISTS SolicitudesRestablecimiento2FA (Email varchar(254) NOT NULL PRIMARY KEY, Status varchar(20) NOT NULL, RequestedAt datetime NOT NULL, ResolvedAt datetime NULL, ResolvedBy varchar(254) NULL) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;"); } catch { }
            }
            else
            {
                await ExecuteAsync("""
                IF OBJECT_ID(N'dbo.SeguridadUsuarios',N'U') IS NULL CREATE TABLE dbo.SeguridadUsuarios(Email nvarchar(254) NOT NULL PRIMARY KEY,EmailConfirmed bit NOT NULL DEFAULT 0,TwoFactorEnabled bit NOT NULL DEFAULT 0,TotpSecret nvarchar(128) NULL,FailedLoginAttempts int NOT NULL DEFAULT 0,LockoutEnd datetime2 NULL,ExternalProvider nvarchar(40) NULL,ExternalProviderId nvarchar(255) NULL,UpdatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
                """);
                await ExecuteAsync("""
                IF COL_LENGTH('dbo.SeguridadUsuarios','PasswordSetupRequired') IS NULL ALTER TABLE dbo.SeguridadUsuarios ADD PasswordSetupRequired bit NOT NULL CONSTRAINT DF_SeguridadUsuarios_PasswordSetupRequired DEFAULT 0;
                IF COL_LENGTH('dbo.SeguridadUsuarios','PasswordSetupMigrated') IS NULL ALTER TABLE dbo.SeguridadUsuarios ADD PasswordSetupMigrated bit NOT NULL CONSTRAINT DF_SeguridadUsuarios_PasswordSetupMigrated DEFAULT 0;
                """);
                try { await ExecuteAsync("IF COL_LENGTH('dbo.SeguridadUsuarios','SessionVersion') IS NULL ALTER TABLE dbo.SeguridadUsuarios ADD SessionVersion int NOT NULL CONSTRAINT DF_SeguridadUsuarios_SessionVersion DEFAULT 0;"); } catch { }
                await ExecuteAsync("""
                INSERT INTO dbo.SeguridadUsuarios(Email,EmailConfirmed) SELECT LOWER(u.Email),1 FROM dbo.Usuarios u WHERE NOT EXISTS(SELECT 1 FROM dbo.SeguridadUsuarios s WHERE LOWER(s.Email)=LOWER(u.Email));
                UPDATE dbo.SeguridadUsuarios SET PasswordSetupRequired=CASE WHEN ExternalProvider='Google' THEN 1 ELSE 0 END,PasswordSetupMigrated=1 WHERE PasswordSetupMigrated=0;
                IF OBJECT_ID(N'dbo.TokensConfirmacionCorreo',N'U') IS NULL CREATE TABLE dbo.TokensConfirmacionCorreo(TokenId int IDENTITY PRIMARY KEY,Email nvarchar(254) NOT NULL,TokenHash char(64) NOT NULL UNIQUE,ExpiresAt datetime2 NOT NULL,UsedAt datetime2 NULL,CreatedAt datetime2 NOT NULL);
                """);
                try { await ExecuteAsync("IF OBJECT_ID(N'dbo.SolicitudesRestablecimiento2FA',N'U') IS NULL CREATE TABLE dbo.SolicitudesRestablecimiento2FA(Email nvarchar(254) NOT NULL PRIMARY KEY,Status nvarchar(20) NOT NULL,RequestedAt datetime2 NOT NULL,ResolvedAt datetime2 NULL,ResolvedBy nvarchar(254) NULL);"); } catch { }
            }

            if (UseMySql)
            {
                if (Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='SeguridadUsuarios' AND COLUMN_NAME='IsTestAccount';")) == 0)
                    await ExecuteAsync("ALTER TABLE SeguridadUsuarios ADD IsTestAccount bit NOT NULL DEFAULT 0;");
            }
            else
                await ExecuteAsync("IF COL_LENGTH('dbo.SeguridadUsuarios','IsTestAccount') IS NULL ALTER TABLE dbo.SeguridadUsuarios ADD IsTestAccount bit NOT NULL DEFAULT 0;");
            _authenticationSchemaReady = true;
        }
        finally
        {
            AuthenticationSchemaLock.Release();
        }
    }

    private static bool VerifyTotp(string secret, string code)
    {
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (code.Length != 6) return false;
        var key = Base32Decode(secret);
        var timestep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counter = new byte[8];
        for (var offset = -1; offset <= 1; offset++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, timestep + offset);
            var hash = HMACSHA1.HashData(key, counter);
            var index = hash[^1] & 0x0f;
            var value = ((hash[index] & 0x7f) << 24) | ((hash[index + 1] & 0xff) << 16) | ((hash[index + 2] & 0xff) << 8) | (hash[index + 3] & 0xff);
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes((value % 1_000_000).ToString("D6")), Encoding.ASCII.GetBytes(code))) return true;
        }
        return false;
    }

    private static string Base32Encode(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder(); var buffer = 0; var bits = 0;
        foreach (var b in bytes) { buffer = (buffer << 8) | b; bits += 8; while (bits >= 5) { output.Append(alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; } }
        if (bits > 0) output.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    private static byte[] Base32Decode(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(); var buffer = 0; var bits = 0;
        foreach (var c in value.Trim().TrimEnd('=').ToUpperInvariant()) { var index = alphabet.IndexOf(c); if (index < 0) continue; buffer = (buffer << 5) | index; bits += 5; if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 255)); bits -= 8; } }
        return bytes.ToArray();
    }

    public sealed record UserSecurityState(bool EmailConfirmed, bool TwoFactorEnabled, string? TotpSecret, bool PasswordSetupRequired);
    public sealed record SecureAuthResult(AuthUser? User, SecureAuthStatus Status, DateTime? LockoutEnd, UserSecurityState? Security);
    public enum SecureAuthStatus { Success, Invalid, Locked, EmailNotConfirmed, RequiresTwoFactor }
}
