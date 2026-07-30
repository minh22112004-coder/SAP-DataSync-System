using SapDataSync.Importer;

var options = ImporterOptions.FromEnvironment();
using var shutdown = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

Console.WriteLine("SAP DataSync Importer started.");
Console.WriteLine("Source directory: {0}", options.SourcePath);
Console.WriteLine("File pattern: {0}", options.FilePattern);
Console.WriteLine("Worksheet: {0}", options.WorksheetName ?? "first worksheet");
Console.WriteLine("Poll interval: {0} seconds", options.PollSeconds);
Console.WriteLine("Minimum file age: {0} seconds", options.MinimumFileAgeSeconds);
Console.WriteLine("The source directory is read-only; the importer never writes to Excel files.");

var importService = new ExcelImportService(options.ConnectionString, options.BatchSize);
var completedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

while (!shutdown.IsCancellationRequested)
{
    if (!Directory.Exists(options.SourcePath))
    {
        Console.WriteLine("[{0:O}] Source directory is not available: {1}", DateTimeOffset.UtcNow, options.SourcePath);
    }
    else
    {
        var filePaths = Directory
            .EnumerateFiles(options.SourcePath, options.FilePattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var filePath in filePaths)
        {
            if (shutdown.IsCancellationRequested)
            {
                break;
            }

            var info = new FileInfo(filePath);
            var age = DateTime.UtcNow - info.LastWriteTimeUtc;
            if (age < TimeSpan.FromSeconds(options.MinimumFileAgeSeconds))
            {
                Console.WriteLine("[{0:O}] Waiting for file to stabilize: {1}", DateTimeOffset.UtcNow, info.Name);
                continue;
            }

            try
            {
                var hash = await ExcelImportService.ComputeSha256Async(filePath, shutdown.Token);
                if (completedFiles.TryGetValue(filePath, out var completedHash) && completedHash == hash)
                {
                    continue;
                }

                var result = await importService.ImportAsync(filePath, hash, options.WorksheetName, shutdown.Token);
                if (result.Status is ImportStatus.Completed or ImportStatus.AlreadyCompleted)
                {
                    completedFiles[filePath] = hash;
                }

                Console.WriteLine(
                    "[{0:O}] {1}: file={2}; total={3}; inserted={4}; updated={5}; unchanged={6}; importLogId={7}",
                    DateTimeOffset.UtcNow,
                    result.Status,
                    info.Name,
                    result.TotalRows,
                    result.InsertedRows,
                    result.UpdatedRows,
                    result.UnchangedRows,
                    result.ImportLogId?.ToString() ?? "-");
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    "[{0:O}] Import failed for {1}: {2}",
                    DateTimeOffset.UtcNow,
                    info.Name,
                    exception.Message);
            }
        }
    }

    if (options.RunOnce)
    {
        break;
    }

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), shutdown.Token);
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
        break;
    }
}

Console.WriteLine("SAP DataSync Importer stopped.");
