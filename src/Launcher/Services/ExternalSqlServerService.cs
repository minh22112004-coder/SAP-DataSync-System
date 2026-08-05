using System.IO;
using Microsoft.Data.SqlClient;
using SapDataSync.Launcher.Models;

namespace SapDataSync.Launcher.Services;

public sealed class ExternalSqlServerService(ProjectLocation location)
{
    public async Task<ServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> settings;
        try
        {
            settings = EnvironmentFile.Read(location.RootDirectory);
        }
        catch (FileNotFoundException)
        {
            return Status(ServiceState.Unknown, "Chưa cấu hình kết nối");
        }

        if (!TryRequired(settings, "SQL_HOST", out var configuredHost) ||
            !TryRequired(settings, "SQL_PASSWORD", out var password))
        {
            return Status(ServiceState.Unknown, "Thiếu cấu hình SQL Server");
        }

        var host = configuredHost.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase)
            ? "127.0.0.1"
            : configuredHost;
        var port = ReadInt(settings, "SQL_PORT", 1433, 1, 65535);
        var database = Read(settings, "SQL_DATABASE", "SapDataSync");
        var user = Read(settings, "SQL_USER", "sa");
        var timeout = ReadInt(settings, "SQL_CONNECT_TIMEOUT_SECONDS", 5, 1, 60);

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            InitialCatalog = "master",
            UserID = user,
            Password = password,
            Encrypt = ReadBool(settings, "SQL_ENCRYPT", true),
            TrustServerCertificate = ReadBool(settings, "SQL_TRUST_SERVER_CERTIFICATE", true),
            ConnectTimeout = timeout,
            PersistSecurityInfo = false,
            ApplicationName = "SAP DataSync Launcher"
        };

        try
        {
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    CONVERT(int, SERVERPROPERTY('ProductMajorVersion')),
                    CASE WHEN DB_ID(@DatabaseName) IS NULL THEN 0 ELSE 1 END;
                """;
            command.Parameters.AddWithValue("@DatabaseName", database);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return Status(ServiceState.Unhealthy, "Không đọc được trạng thái database");
            }

            var majorVersion = reader.GetInt32(0);
            if (majorVersion != 16)
            {
                return Status(ServiceState.Unhealthy, $"Sai phiên bản · yêu cầu SQL Server 2022");
            }

            return reader.GetInt32(1) == 1
                ? Status(ServiceState.Healthy, $"Đã kết nối · {database}")
                : Status(ServiceState.Starting, "Đã kết nối · chờ khởi tạo database");
        }
        catch (SqlException exception) when (exception.Number == 18456)
        {
            return Status(ServiceState.Unhealthy, "Sai tài khoản hoặc mật khẩu SQL");
        }
        catch (SqlException)
        {
            return Status(ServiceState.Unhealthy, $"Không kết nối được {host}:{port}");
        }
        catch (ArgumentException)
        {
            return Status(ServiceState.Unknown, "Cấu hình SQL Server không hợp lệ");
        }
    }

    private static ServiceStatus Status(ServiceState state, string detail) =>
        new("sqlserver", state, detail);

    private static bool TryRequired(
        IReadOnlyDictionary<string, string> settings,
        string name,
        out string value)
    {
        if (settings.TryGetValue(name, out var configured) && !string.IsNullOrWhiteSpace(configured))
        {
            value = configured;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string Read(
        IReadOnlyDictionary<string, string> settings,
        string name,
        string defaultValue) =>
        settings.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : defaultValue;

    private static int ReadInt(
        IReadOnlyDictionary<string, string> settings,
        string name,
        int defaultValue,
        int minimum,
        int maximum) =>
        settings.TryGetValue(name, out var value) && int.TryParse(value, out var parsed) &&
        parsed >= minimum && parsed <= maximum
            ? parsed
            : defaultValue;

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> settings,
        string name,
        bool defaultValue) =>
        settings.TryGetValue(name, out var value) && bool.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;
}
