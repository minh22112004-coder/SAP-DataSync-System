using System.IO;
using System.Text;

namespace SapDataSync.Launcher.Services;

public sealed record EnvironmentSetupResult(bool Created, string Message);

public static class EnvironmentSetupService
{
    private static readonly string[] RequiredSettings =
    [
        "SQL_HOST",
        "SQL_PORT",
        "SQL_DATABASE",
        "SQL_USER",
        "SQL_PASSWORD"
    ];

    public static EnvironmentSetupResult EnsureConfigured(string rootDirectory)
    {
        var environmentPath = Path.Combine(rootDirectory, ".env");
        if (File.Exists(environmentPath))
        {
            return new EnvironmentSetupResult(false, "Cấu hình hệ thống đã tồn tại.");
        }

        var examplePath = Path.Combine(rootDirectory, ".env.example");
        if (!File.Exists(examplePath))
        {
            throw new FileNotFoundException(
                "Không tìm thấy .env.example để tạo cấu hình lần đầu.",
                examplePath);
        }

        var lines = File.ReadAllLines(examplePath, Encoding.UTF8);
        var settings = EnvironmentFile.ParseLines(lines);
        var missingSettings = RequiredSettings
            .Where(name => !settings.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (missingSettings.Length > 0)
        {
            throw new InvalidDataException(
                $".env.example thiếu cấu hình: {string.Join(", ", missingSettings)}.");
        }

        var temporaryPath = $"{environmentPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllLines(temporaryPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, environmentPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        Directory.CreateDirectory(Path.Combine(rootDirectory, "data", "source"));
        return new EnvironmentSetupResult(
            true,
            "Đã tạo cấu hình kết nối SQL Server từ mẫu. Launcher không thay đổi mật khẩu SQL Server.");
    }
}
