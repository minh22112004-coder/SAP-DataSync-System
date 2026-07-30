using System.Text.Json.Serialization;

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

public sealed class AiPlanRequest
{
    public string? Goal { get; init; }
    public SapDataQuery? Query { get; init; } = new();

    public Dictionary<string, string[]>? Validate()
    {
        var errors = Query?.Validate() ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (Query is null)
        {
            errors[nameof(Query)] = ["Bộ lọc dữ liệu là bắt buộc."];
        }
        if (Goal?.Length > 500)
        {
            errors[nameof(Goal)] = ["Mục tiêu kế hoạch không được vượt quá 500 ký tự."];
        }

        return errors.Count == 0 ? null : errors;
    }
}

public sealed record AiStatus(bool Enabled, string Provider, string Model, int MaxRecords);

public sealed class AiGeneratedPlan
{
    public string Title { get; init; } = string.Empty;
    public string ExecutiveSummary { get; init; } = string.Empty;
    public List<AiPlanAction> Actions { get; set; } = [];
    public List<string> Risks { get; set; } = [];
    public List<string> Assumptions { get; set; } = [];
}

public sealed record AiPlanAction(
    int Priority,
    string Action,
    string Reason,
    List<string> RelatedShippingInstructionIds);

public sealed record AiPlanResponse(
    AiGeneratedPlan Plan,
    string Provider,
    string Model,
    int AnalyzedRecords,
    long TotalMatchingRecords,
    DateTimeOffset GeneratedAt,
    string Disclaimer);

public sealed class AiFilterRequest
{
    public string Question { get; init; } = string.Empty;

    public Dictionary<string, string[]>? Validate()
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(Question))
        {
            errors[nameof(Question)] = ["Hãy nhập câu hỏi cần chuyển thành bộ lọc."];
        }
        else if (Question.Trim().Length > 500)
        {
            errors[nameof(Question)] = ["Câu hỏi không được vượt quá 500 ký tự."];
        }

        return errors.Count == 0 ? null : errors;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AiFilterDraft
{
    public string? Product { get; init; }
    public string? SalesOrganization { get; init; }
    public List<string> BusinessScenarios { get; init; } = [];
    public string? SiStatus { get; init; }
    public string? SalesOffice { get; init; }
    public string? PlantCode { get; init; }
    public string? SiId { get; init; }
    public string? Customer { get; init; }
    public string? OilSc { get; init; }
    public string? OilSo { get; init; }
    public string? OilPo { get; init; }
    public string? Search { get; init; }
    public string? CreatedFrom { get; init; }
    public string? CreatedTo { get; init; }
    public string? SortBy { get; init; }
    public string? SortDirection { get; init; }
    public string Summary { get; init; } = string.Empty;
    public List<string> Assumptions { get; init; } = [];
}

public sealed record AiFilterResponse(
    SapDataQuery Query,
    string Summary,
    IReadOnlyList<string> Assumptions,
    string Provider,
    string Model,
    bool RequiresConfirmation);
