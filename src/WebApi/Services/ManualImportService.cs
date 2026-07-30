using System.Net;
using System.Net.Http.Json;
using SapDataSync.WebApi.Models;

namespace SapDataSync.WebApi.Services;

public sealed class ManualImportService(HttpClient httpClient)
{
    public async Task<(HttpStatusCode StatusCode, ManualImportStatus Status)> StartAsync(
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync("run", content: null, cancellationToken);
        var status = await response.Content.ReadFromJsonAsync<ManualImportStatus>(cancellationToken)
            ?? throw new InvalidOperationException("ETL Worker returned an empty manual-import response.");
        return (response.StatusCode, status);
    }

    public async Task<ManualImportStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        return await httpClient.GetFromJsonAsync<ManualImportStatus>("status", cancellationToken)
            ?? throw new InvalidOperationException("ETL Worker returned an empty status response.");
    }
}
