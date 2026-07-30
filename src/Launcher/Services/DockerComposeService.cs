using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SapDataSync.Launcher.Models;

namespace SapDataSync.Launcher.Services;

public sealed class DockerComposeService(ProjectLocation location, CommandRunner commandRunner)
{
    private static readonly string[] ExpectedServices = ["sqlserver", "web-api", "etl-worker"];

    public ProjectLocation Location { get; } = location;

    public async Task<SystemSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var versionResult = await commandRunner.RunAsync(
            "docker",
            ["version", "--format", "{{.Server.Version}}"],
            Location.RootDirectory,
            cancellationToken);

        if (!versionResult.Succeeded)
        {
            var cliMissing = versionResult.ExitCode == -1;
            return new SystemSnapshot(
                DockerAvailable: !cliMissing,
                DockerRunning: false,
                cliMissing ? "Chưa tìm thấy Docker Desktop." : "Docker Desktop chưa sẵn sàng.",
                CreateUnknownServices());
        }

        var composeResult = await RunComposeAsync(
            ["ps", "-a", "--format", "json"],
            cancellationToken);

        if (!composeResult.Succeeded)
        {
            return new SystemSnapshot(
                DockerAvailable: true,
                DockerRunning: true,
                "Docker đang chạy nhưng chưa đọc được trạng thái hệ thống.",
                CreateUnknownServices());
        }

        var services = ParseServices(composeResult.StandardOutput);
        return new SystemSnapshot(
            DockerAvailable: true,
            DockerRunning: true,
            $"Docker {versionResult.StandardOutput.Trim()} đang hoạt động.",
            services);
    }

    public async Task<CommandResult> StartAsync(bool buildImages, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "up", "-d" };
        if (buildImages)
        {
            arguments.Add("--build");
        }

        return await RunComposeAsync(arguments, cancellationToken);
    }

    public Task<CommandResult> StopAsync(CancellationToken cancellationToken) =>
        RunComposeAsync(["stop"], cancellationToken);

    public async Task<bool> HasExistingContainersAsync(CancellationToken cancellationToken)
    {
        var result = await RunComposeAsync(["ps", "-a", "-q"], cancellationToken);
        return result.Succeeded && !string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    public async Task<bool> WaitUntilReadyAsync(
        TimeSpan timeout,
        Action<SystemSnapshot>? onSnapshot,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(startedAt) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await GetSnapshotAsync(cancellationToken);
            onSnapshot?.Invoke(snapshot);
            if (snapshot.IsHealthy)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        return false;
    }

    public static bool TryStartDockerDesktop()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var executable = Path.Combine(programFiles, "Docker", "Docker", "Docker Desktop.exe");
        if (!File.Exists(executable))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private Task<CommandResult> RunComposeAsync(
        IEnumerable<string> composeArguments,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "compose", "-f", Location.ComposeFile };
        arguments.AddRange(composeArguments);
        return commandRunner.RunAsync("docker", arguments, Location.RootDirectory, cancellationToken);
    }

    private static IReadOnlyDictionary<string, ServiceStatus> ParseServices(string json)
    {
        var parsed = new Dictionary<string, ServiceStatus>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in document.RootElement.EnumerateArray())
                    {
                        AddService(element, parsed);
                    }
                }
                else if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    AddService(document.RootElement, parsed);
                }
            }
            catch (JsonException)
            {
                foreach (var line in json.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        using var lineDocument = JsonDocument.Parse(line);
                        AddService(lineDocument.RootElement, parsed);
                    }
                    catch (JsonException)
                    {
                        // Ignore malformed lines and report unknown for missing services.
                    }
                }
            }
        }

        foreach (var expectedService in ExpectedServices)
        {
            parsed.TryAdd(
                expectedService,
                new ServiceStatus(expectedService, ServiceState.Stopped, "Chưa khởi động"));
        }

        return parsed;
    }

    private static void AddService(JsonElement element, IDictionary<string, ServiceStatus> services)
    {
        var service = ReadString(element, "Service");
        if (string.IsNullOrWhiteSpace(service))
        {
            return;
        }

        var stateText = ReadString(element, "State");
        var healthText = ReadString(element, "Health");
        var state = MapState(stateText, healthText);
        var detail = state switch
        {
            ServiceState.Healthy => "Đang chạy · ổn định",
            ServiceState.Running => "Đang chạy",
            ServiceState.Starting => "Đang khởi động",
            ServiceState.Unhealthy => "Đang chạy · cần kiểm tra",
            ServiceState.Stopped => "Đã dừng",
            _ => "Chưa xác định"
        };

        services[service] = new ServiceStatus(service, state, detail);
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static ServiceState MapState(string state, string health)
    {
        if (health.Equals("healthy", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceState.Healthy;
        }

        if (health.Equals("unhealthy", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceState.Unhealthy;
        }

        if (health.Equals("starting", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("restarting", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("created", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceState.Starting;
        }

        if (state.Equals("running", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceState.Running;
        }

        if (state.Equals("exited", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("stopped", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("dead", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceState.Stopped;
        }

        return ServiceState.Unknown;
    }

    private static IReadOnlyDictionary<string, ServiceStatus> CreateUnknownServices() =>
        ExpectedServices.ToDictionary(
            service => service,
            service => new ServiceStatus(service, ServiceState.Unknown, "Không đọc được trạng thái"),
            StringComparer.OrdinalIgnoreCase);
}
