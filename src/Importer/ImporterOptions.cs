namespace SapDataSync.Importer;

internal sealed record ImporterOptions(
    string ConnectionString,
    string SourcePath,
    string FilePattern,
    string? WorksheetName,
    int PollSeconds,
    int MinimumFileAgeSeconds,
    int BatchSize,
    bool RunOnce)
{
    public static ImporterOptions FromEnvironment()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings__SqlServer is required.");
        }

        var worksheetName = Environment.GetEnvironmentVariable("SAP_WORKSHEET_NAME");
        if (string.IsNullOrWhiteSpace(worksheetName))
        {
            worksheetName = null;
        }

        return new ImporterOptions(
            connectionString,
            Environment.GetEnvironmentVariable("SAP_SOURCE_PATH") ?? "/data/source",
            Environment.GetEnvironmentVariable("SAP_FILE_PATTERN") ?? "export*.xlsx",
            worksheetName,
            ReadInt("IMPORT_POLL_SECONDS", 30, 5, 86_400),
            ReadInt("IMPORT_MIN_FILE_AGE_SECONDS", 10, 0, 3_600),
            ReadInt("IMPORT_BATCH_SIZE", 500, 1, 10_000),
            bool.TryParse(Environment.GetEnvironmentVariable("IMPORTER_RUN_ONCE"), out var runOnce) && runOnce);
    }

    private static int ReadInt(string name, int defaultValue, int minimum, int maximum)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
        {
            throw new InvalidOperationException($"{name} must be an integer from {minimum} to {maximum}.");
        }

        return value;
    }
}
