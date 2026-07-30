using System.Data;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using SapDataSync.WebApi.Models;

namespace SapDataSync.WebApi.Services;

public sealed record AiRuntimeSettings(
    bool Enabled,
    string Provider,
    string Model,
    string BaseUrl,
    string? ApiKey,
    int MaxRecords);

public sealed class AdminSettingsService
{
    private const string AiApiKeySetting = "AI_API_KEY";
    private const int PasswordIterations = 210_000;
    private readonly IConfiguration configuration;
    private readonly IDataProtector apiKeyProtector;
    private readonly ILogger<AdminSettingsService> logger;

    public AdminSettingsService(
        IConfiguration configuration,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<AdminSettingsService> logger)
    {
        this.configuration = configuration;
        this.logger = logger;
        apiKeyProtector = dataProtectionProvider.CreateProtector("SapDataSync.Stage5.AiApiKey.v1");
    }

    public async Task<AdminStatus> GetStatusAsync(bool authenticated, CancellationToken cancellationToken)
    {
        var setupRequired = !await HasAdminAsync(cancellationToken);
        var settings = await GetAiSettingsAsync(cancellationToken);
        return new AdminStatus(
            SetupRequired: setupRequired,
            Authenticated: authenticated,
            AiConfigured: settings.Enabled,
            ApiKeyMasked: settings.Enabled ? "••••••••••••" : null,
            Provider: settings.Provider,
            Model: settings.Model);
    }

    public async Task CreateAdminAsync(
        string password,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(32);
        var hash = HashPassword(password, salt, PasswordIterations);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM dbo.AdminAccount WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1";
            if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) > 0)
            {
                throw new InvalidOperationException("Tài khoản quản trị đã được thiết lập.");
            }
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT dbo.AdminAccount (Id, PasswordHash, PasswordSalt, PasswordIterations)
                VALUES (1, @Hash, @Salt, @Iterations);
                """;
            insert.Parameters.Add("@Hash", SqlDbType.VarBinary, 64).Value = hash;
            insert.Parameters.Add("@Salt", SqlDbType.VarBinary, 32).Value = salt;
            insert.Parameters.Add("@Iterations", SqlDbType.Int).Value = PasswordIterations;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteAuditAsync(
            connection,
            transaction,
            "AdminSetup",
            "Đã tạo tài khoản quản trị ban đầu.",
            remoteIp,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> ValidateAdminPasswordAsync(
        string password,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        var passwordCanBeHashed = !string.IsNullOrEmpty(password) && password.Length <= 128;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT PasswordHash, PasswordSalt, PasswordIterations
            FROM dbo.AdminAccount
            WHERE Id = 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }

        var expectedHash = (byte[])reader[0];
        var salt = (byte[])reader[1];
        var iterations = reader.GetInt32(2);
        var actualHash = passwordCanBeHashed
            ? HashPassword(password, salt, iterations)
            : RandomNumberGenerator.GetBytes(expectedHash.Length);
        var valid = passwordCanBeHashed && CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        await reader.CloseAsync();

        await WriteAuditAsync(
            connection,
            transaction: null,
            valid ? "AdminLoginSucceeded" : "AdminLoginFailed",
            valid ? "Đăng nhập quản trị thành công." : "Đăng nhập quản trị thất bại.",
            remoteIp,
            cancellationToken);
        return valid;
    }

    public async Task SaveAiApiKeyAsync(
        string apiKey,
        string actor,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        var normalizedKey = apiKey.Trim();
        if (normalizedKey.Length is < 10 or > 500)
        {
            throw new ArgumentException("API key phải có từ 10 đến 500 ký tự.", nameof(apiKey));
        }

        var protectedValue = apiKeyProtector.Protect(normalizedKey);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await UpsertSettingAsync(connection, transaction, protectedValue, actor, cancellationToken);
        await WriteAuditAsync(
            connection,
            transaction,
            "AiApiKeyUpdated",
            "Đã thêm hoặc thay đổi API key AI. Giá trị key không được ghi audit.",
            remoteIp,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveAiApiKeyAsync(
        string actor,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await UpsertSettingAsync(connection, transaction, protectedValue: null, actor, cancellationToken);
        await WriteAuditAsync(
            connection,
            transaction,
            "AiApiKeyRemoved",
            "Đã xóa API key AI và tắt chức năng AI.",
            remoteIp,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task WriteLogoutAuditAsync(string? remoteIp, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await WriteAuditAsync(
            connection,
            transaction: null,
            "AdminLogout",
            "Đã đăng xuất phiên quản trị.",
            remoteIp,
            cancellationToken);
    }

    public async Task<AiRuntimeSettings> GetAiSettingsAsync(CancellationToken cancellationToken)
    {
        var provider = configuration["AI:Provider"] ?? "Groq";
        var model = configuration["AI:Model"] ?? "llama-3.3-70b-versatile";
        var baseUrl = configuration["AI:BaseUrl"] ?? "https://api.groq.com/openai/v1";
        var maxRecords = Math.Clamp(configuration.GetValue("AI:MaxRecords", 50), 10, 100);
        var stored = await ReadStoredApiKeyAsync(cancellationToken);

        string? apiKey;
        bool enabled;
        if (stored.Exists)
        {
            apiKey = stored.ApiKey;
            enabled = !string.IsNullOrWhiteSpace(apiKey);
        }
        else
        {
            apiKey = configuration["AI:ApiKey"];
            enabled = configuration.GetValue("AI:Enabled", false) && !string.IsNullOrWhiteSpace(apiKey);
        }

        return new AiRuntimeSettings(enabled, provider, model, baseUrl, apiKey, maxRecords);
    }

    private async Task<bool> HasAdminAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.AdminAccount WHERE Id = 1) THEN 1 ELSE 0 END";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private async Task<(bool Exists, string? ApiKey)> ReadStoredApiKeyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProtectedValue FROM dbo.AppConfiguration WHERE [Key] = @Key";
        command.Parameters.Add("@Key", SqlDbType.NVarChar, 100).Value = AiApiKeySetting;

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null)
        {
            return (false, null);
        }

        if (result is DBNull || string.IsNullOrWhiteSpace(Convert.ToString(result)))
        {
            return (true, null);
        }

        try
        {
            return (true, apiKeyProtector.Unprotect((string)result));
        }
        catch (CryptographicException exception)
        {
            logger.LogError(exception, "Unable to decrypt the stored AI API key. The key will remain disabled.");
            return (true, null);
        }
    }

    private static async Task UpsertSettingAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string? protectedValue,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            MERGE dbo.AppConfiguration WITH (HOLDLOCK) AS target
            USING (SELECT @Key AS [Key]) AS source
               ON target.[Key] = source.[Key]
            WHEN MATCHED THEN
                UPDATE SET ProtectedValue = @ProtectedValue,
                           UpdatedAt = SYSUTCDATETIME(),
                           UpdatedBy = @UpdatedBy
            WHEN NOT MATCHED THEN
                INSERT ([Key], ProtectedValue, UpdatedBy)
                VALUES (@Key, @ProtectedValue, @UpdatedBy);
            """;
        command.Parameters.Add("@Key", SqlDbType.NVarChar, 100).Value = AiApiKeySetting;
        command.Parameters.Add("@ProtectedValue", SqlDbType.NVarChar, -1).Value =
            protectedValue is null ? DBNull.Value : protectedValue;
        command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 100).Value = actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var configured = configuration.GetConnectionString("SqlServer")
            ?? throw new InvalidOperationException("ConnectionStrings:SqlServer is required.");
        var connectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "SapDataSync"
        }.ConnectionString;
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task WriteAuditAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string eventType,
        string detail,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT dbo.AdminAuditLog (EventType, Detail, RemoteIp)
            VALUES (@EventType, @Detail, @RemoteIp);
            """;
        command.Parameters.Add("@EventType", SqlDbType.NVarChar, 50).Value = eventType;
        command.Parameters.Add("@Detail", SqlDbType.NVarChar, 500).Value = detail;
        command.Parameters.Add("@RemoteIp", SqlDbType.NVarChar, 64).Value =
            string.IsNullOrWhiteSpace(remoteIp) ? DBNull.Value : remoteIp;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static byte[] HashPassword(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            64);

    private static void ValidatePassword(string password)
    {
        if (password.Length is < 12 or > 128)
        {
            throw new ArgumentException("Mật khẩu quản trị phải có từ 12 đến 128 ký tự.", nameof(password));
        }

        if (!password.Any(char.IsUpper) ||
            !password.Any(char.IsLower) ||
            !password.Any(char.IsDigit) ||
            !password.Any(character => !char.IsLetterOrDigit(character)))
        {
            throw new ArgumentException(
                "Mật khẩu phải có chữ hoa, chữ thường, chữ số và ký tự đặc biệt.",
                nameof(password));
        }
    }
}
