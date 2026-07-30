namespace SapDataSync.WebApi.Models;

public sealed class SapDataQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public string? Search { get; init; }
    public string? Product { get; init; } = "12";
    public string? SalesOrganization { get; init; } = "SG50";
    public string? BusinessScenario { get; init; }
    public string? SiStatus { get; init; }
    public string? SalesOffice { get; init; }
    public string? PlantCode { get; init; }
    public string? SiId { get; init; }
    public string? Customer { get; init; }
    public string? OilSc { get; init; }
    public string? OilSo { get; init; }
    public string? OilPo { get; init; }
    public DateOnly? CreatedFrom { get; init; }
    public DateOnly? CreatedTo { get; init; }
    public string? SortBy { get; init; } = "createdDate";
    public string? SortDirection { get; init; } = "desc";

    public Dictionary<string, string[]>? Validate()
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (Page < 1)
        {
            errors[nameof(Page)] = ["Page phải lớn hơn hoặc bằng 1."];
        }

        if (PageSize is < 10 or > 200)
        {
            errors[nameof(PageSize)] = ["PageSize phải nằm trong khoảng 10–200."];
        }

        if (CreatedFrom.HasValue && CreatedTo.HasValue && CreatedFrom > CreatedTo)
        {
            errors[nameof(CreatedTo)] = ["Ngày kết thúc không được nhỏ hơn ngày bắt đầu."];
        }

        if (Search?.Length > 200)
        {
            errors[nameof(Search)] = ["Nội dung tìm kiếm không được vượt quá 200 ký tự."];
        }

        return errors.Count == 0 ? null : errors;
    }
}

public sealed class ImportLogQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public string? Search { get; init; }
    public string? Status { get; init; }

    public Dictionary<string, string[]>? Validate()
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (Page < 1)
        {
            errors[nameof(Page)] = ["Page phải lớn hơn hoặc bằng 1."];
        }

        if (PageSize is < 10 or > 100)
        {
            errors[nameof(PageSize)] = ["PageSize phải nằm trong khoảng 10–100."];
        }

        return errors.Count == 0 ? null : errors;
    }
}

public sealed class ChangeLogQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 10;
    public string? ChangeType { get; init; }

    public Dictionary<string, string[]>? Validate()
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (Page < 1)
        {
            errors[nameof(Page)] = ["Page phải lớn hơn hoặc bằng 1."];
        }

        if (PageSize is < 1 or > 20)
        {
            errors[nameof(PageSize)] = ["PageSize phải nằm trong khoảng 1–20."];
        }

        if (!string.IsNullOrWhiteSpace(ChangeType) &&
            ChangeType is not ("Insert" or "Update" or "Delete"))
        {
            errors[nameof(ChangeType)] = ["ChangeType phải là Insert, Update hoặc Delete."];
        }

        return errors.Count == 0 ? null : errors;
    }
}

public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalItems,
    int TotalPages);

public sealed record SapDataListItem(
    long Id,
    string? Product,
    string? SalesOrganization,
    string? ShippingInstructionsId,
    string? UniqueNumber,
    string? SiStatus,
    string? BusinessScenario,
    string? CustomerName,
    string? SellingPlant,
    string? PlantCode,
    string? SalesOffice,
    string? GradeDescription,
    string? Quantity,
    string? Uom,
    string? OilSc,
    string? OilSo,
    string? OilPo,
    string? FirstCommittedShipDate,
    string? EstimatedDeparture,
    string? EstimatedArrival,
    string? SiCreatedOn,
    DateTime UpdatedAt);

public sealed record SapDataDetail(
    long Id,
    Guid ImportLogId,
    int SourceRowNumber,
    string? Product,
    string? SalesOrganization,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyDictionary<string, string?> Fields);

public sealed record FilterOptions(
    IReadOnlyList<string> Products,
    IReadOnlyList<string> SalesOrganizations,
    IReadOnlyList<string> BusinessScenarios,
    IReadOnlyList<string> SiStatuses,
    IReadOnlyList<string> SalesOffices,
    IReadOnlyList<string> PlantCodes);

public sealed record ImportLogListItem(
    Guid Id,
    string FileName,
    string Status,
    string? Product,
    string? SalesOrganization,
    DateTime StartedAt,
    DateTime? CompletedAt,
    int TotalRows,
    int InsertedRows,
    int UpdatedRows,
    int DeletedRows,
    int UnchangedRows,
    int ErrorRows);

public sealed record ImportLogDetail(
    Guid Id,
    string FileName,
    string FileHash,
    string Status,
    string? Product,
    string? SalesOrganization,
    DateTime StartedAt,
    DateTime? CompletedAt,
    int TotalRows,
    int InsertedRows,
    int UpdatedRows,
    int DeletedRows,
    int UnchangedRows,
    int ErrorRows,
    bool SoftDeleteEnabled,
    string? ErrorMessage,
    DateTime CreatedAt);

public sealed record FieldChange(
    string Field,
    string? OldValue,
    string? NewValue);

public sealed record SapDataChangeListItem(
    long Id,
    long SapDataId,
    int? SourceRowNumber,
    string? ShippingInstructionsId,
    string? UniqueNumber,
    string ChangeType,
    DateTime CreatedAt,
    int ChangedFieldCount,
    IReadOnlyList<FieldChange> Fields);

public sealed record ManualImportStatus(
    bool Running,
    string? Trigger,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int? ExitCode,
    string Message);
