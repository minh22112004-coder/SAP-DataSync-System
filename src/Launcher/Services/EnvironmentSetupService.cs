using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SapDataSync.Launcher.Services;

public sealed record EnvironmentSetupResult(bool Created, string Message);

public static class EnvironmentSetupService
{
    private const string PasswordPrefix = "MSSQL_SA_PASSWORD=";

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
        var replacedPassword = false;

        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].TrimStart().StartsWith(PasswordPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            lines[index] = $"{PasswordPrefix}{CreateStrongPassword()}";
            replacedPassword = true;
            break;
        }

        if (!replacedPassword)
        {
            throw new InvalidDataException(".env.example không có cấu hình MSSQL_SA_PASSWORD.");
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
            "Đã tự động tạo cấu hình lần đầu với mật khẩu database ngẫu nhiên an toàn.");
    }

    private static string CreateStrongPassword()
    {
        var randomPart = Convert.ToHexString(RandomNumberGenerator.GetBytes(18));
        return $"Sap!{randomPart}a9";
    }
}
