using System.IO;

namespace SapDataSync.Launcher.Services;

internal static class EnvironmentFile
{
    public static IReadOnlyDictionary<string, string> Read(string rootDirectory)
    {
        var path = Path.Combine(rootDirectory, ".env");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Chưa có file cấu hình .env.", path);
        }

        return ParseLines(File.ReadLines(path));
    }

    public static IReadOnlyDictionary<string, string> ParseLines(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return values;
    }
}
