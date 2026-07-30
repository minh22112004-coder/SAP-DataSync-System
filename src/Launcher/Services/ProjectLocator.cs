using System.IO;

namespace SapDataSync.Launcher.Services;

public sealed record ProjectLocation(string RootDirectory, string ComposeFile, Uri WebUri);

public static class ProjectLocator
{
    private const string ComposeFileName = "compose.yaml";

    public static ProjectLocation? Find()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("SAPDATASYNC_ROOT");
        var candidates = new[]
        {
            configuredRoot,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        };

        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var root = FindFrom(candidate!);
            if (root is not null)
            {
                return new ProjectLocation(
                    root,
                    Path.Combine(root, ComposeFileName),
                    new Uri($"http://localhost:{ReadWebPort(root)}"));
            }
        }

        return null;
    }

    private static string? FindFrom(string startPath)
    {
        DirectoryInfo? directory;

        try
        {
            var fullPath = Path.GetFullPath(startPath);
            directory = new DirectoryInfo(File.Exists(fullPath)
                ? Path.GetDirectoryName(fullPath)!
                : fullPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, ComposeFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static int ReadWebPort(string rootDirectory)
    {
        var environmentFile = Path.Combine(rootDirectory, ".env");
        if (!File.Exists(environmentFile))
        {
            environmentFile = Path.Combine(rootDirectory, ".env.example");
        }

        if (!File.Exists(environmentFile))
        {
            return 8080;
        }

        try
        {
            foreach (var rawLine in File.ReadLines(environmentFile))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("WEB_PORT=", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line["WEB_PORT=".Length..].Trim(), out var port) &&
                    port is > 0 and <= 65535)
                {
                    return port;
                }
            }
        }
        catch (IOException)
        {
            // Fall back to the Compose default.
        }

        return 8080;
    }
}
