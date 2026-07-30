namespace SapDataSync.Launcher.Models;

public enum ServiceState
{
    Unknown,
    Stopped,
    Starting,
    Running,
    Healthy,
    Unhealthy
}

public sealed record ServiceStatus(
    string Service,
    ServiceState State,
    string Detail);

public sealed record SystemSnapshot(
    bool DockerAvailable,
    bool DockerRunning,
    string DockerDetail,
    IReadOnlyDictionary<string, ServiceStatus> Services)
{
    public bool IsHealthy =>
        DockerRunning &&
        Services.Count > 0 &&
        Services.Values.All(service => service.State is ServiceState.Healthy or ServiceState.Running);
}
