namespace SapDataSync.Importer;

internal enum ImportStatus
{
    Completed,
    AlreadyCompleted
}

internal sealed record ImportResult(
    ImportStatus Status,
    Guid? ImportLogId,
    int TotalRows,
    int InsertedRows,
    int UpdatedRows,
    int UnchangedRows);
