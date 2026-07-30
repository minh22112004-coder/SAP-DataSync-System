using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SapDataSync.WebApi.Infrastructure;

public sealed partial class DatabaseBootstrapper(
    IConfiguration configuration,
    ILogger<DatabaseBootstrapper> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("SqlServer")
            ?? throw new InvalidOperationException("ConnectionStrings:SqlServer is required when database initialization is enabled.");

        var scriptsPath = configuration["Database:SchemaScriptsPath"]
            ?? "database/scripts";
        var maxAttempts = Math.Max(1, configuration.GetValue("Database:MaxAttempts", 30));
        var retryDelaySeconds = Math.Max(1, configuration.GetValue("Database:RetryDelaySeconds", 2));

        if (!Directory.Exists(scriptsPath))
        {
            throw new DirectoryNotFoundException($"The database schema scripts directory was not found: {scriptsPath}");
        }

        var scriptPaths = Directory
            .EnumerateFiles(scriptsPath, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (scriptPaths.Length == 0)
        {
            throw new InvalidOperationException($"No .sql schema scripts were found in: {scriptsPath}");
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);

                foreach (var scriptPath in scriptPaths)
                {
                    var script = await File.ReadAllTextAsync(scriptPath, cancellationToken);
                    var batches = GoBatchSeparator()
                        .Split(script)
                        .Where(batch => !string.IsNullOrWhiteSpace(batch));

                    foreach (var batch in batches)
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText = batch;
                        command.CommandTimeout = 120;
                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }

                    logger.LogInformation("Applied database script {ScriptPath}.", scriptPath);
                }

                logger.LogInformation("Database schema is ready after {Attempt} attempt(s).", attempt);
                return;
            }
            catch (Exception exception) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    exception,
                    "Database initialization attempt {Attempt}/{MaxAttempts} failed. Retrying in {DelaySeconds} seconds.",
                    attempt,
                    maxAttempts,
                    retryDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), cancellationToken);
            }
        }

        throw new InvalidOperationException("Database initialization failed after all retry attempts.");
    }

    [GeneratedRegex(@"^\s*GO\s*(?:--.*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoBatchSeparator();
}
