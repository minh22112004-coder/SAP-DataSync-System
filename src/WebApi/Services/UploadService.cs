using System.Security.Cryptography;
using System.IO.Compression;

namespace SapDataSync.WebApi.Services;

public sealed record UploadResult(
    string OriginalFileName,
    string StoredFileName,
    string Sha256,
    long SizeBytes,
    bool AlreadyExisted);

public sealed class UploadService(
    IConfiguration configuration,
    ILogger<UploadService> logger)
{
    public bool Enabled => configuration.GetValue("Uploads:Enabled", false);
    public long MaxBytes => Math.Clamp(
        configuration.GetValue<long>("Uploads:MaxBytes", 100 * 1024 * 1024),
        1024 * 1024,
        500L * 1024 * 1024);

    public async Task<UploadResult> SaveAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (!Enabled) throw new InvalidOperationException("Upload is disabled.");
        if (file.Length <= 0) throw new ArgumentException("File Excel đang trống.");
        if (file.Length > MaxBytes)
        {
            throw new ArgumentException($"File vượt quá giới hạn {MaxBytes / 1024 / 1024} MB.");
        }

        var originalFileName = Path.GetFileName(file.FileName);
        if (!string.Equals(Path.GetExtension(originalFileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Chỉ chấp nhận file Excel có phần mở rộng .xlsx.");
        }

        var uploadPath = configuration["Uploads:Path"] ?? "/data/uploads";
        var temporaryPath = Path.Combine(uploadPath, $".{Guid.NewGuid():N}.uploading");

        try
        {
            Directory.CreateDirectory(uploadPath);
            await using (var target = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await file.CopyToAsync(target, cancellationToken);
            }

            await ValidateOpenXmlSignatureAsync(temporaryPath, cancellationToken);
            await using var hashStream = File.OpenRead(temporaryPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken));
            var existingPath = Directory
                .EnumerateFiles(uploadPath, $"export_upload_*_{hash}.xlsx")
                .FirstOrDefault();
            if (existingPath is not null)
            {
                File.Delete(temporaryPath);
                return new UploadResult(
                    originalFileName, Path.GetFileName(existingPath), hash, file.Length, true);
            }

            var storedFileName = $"export_upload_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{hash}.xlsx";
            var storedPath = Path.Combine(uploadPath, storedFileName);

            if (File.Exists(storedPath))
            {
                File.Delete(temporaryPath);
                return new UploadResult(originalFileName, storedFileName, hash, file.Length, true);
            }

            File.Move(temporaryPath, storedPath);
            return new UploadResult(originalFileName, storedFileName, hash, file.Length, false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            logger.LogError(exception, "Could not persist the uploaded Excel file.");
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task ValidateOpenXmlSignatureAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var signature = new byte[4];
        await using var stream = File.OpenRead(path);
        var bytesRead = await stream.ReadAsync(signature, cancellationToken);
        if (bytesRead != signature.Length || signature[0] != (byte)'P' || signature[1] != (byte)'K')
        {
            throw new ArgumentException("Nội dung file không phải định dạng Excel .xlsx hợp lệ.");
        }

        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.GetEntry("[Content_Types].xml") is null ||
                archive.GetEntry("xl/workbook.xml") is null)
            {
                throw new ArgumentException("File không chứa cấu trúc workbook Excel .xlsx hợp lệ.");
            }
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException("File Excel .xlsx bị lỗi hoặc không đọc được.", exception);
        }
    }
}
