using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using SapDataSync.WebApi.Data;
using SapDataSync.WebApi.Infrastructure;
using SapDataSync.WebApi.Models;
using SapDataSync.WebApi.Services;

var builder = WebApplication.CreateBuilder(args);
var uploadMaxBytes = builder.Configuration.GetValue<long>(
    "Uploads:MaxBytes", 100L * 1024 * 1024);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = uploadMaxBytes);

builder.Services.AddProblemDetails();
builder.Services.AddMemoryCache();
var dataProtectionKeysPath = builder.Configuration["Security:DataProtectionKeysPath"]
    ?? Path.Combine(AppContext.BaseDirectory, "data-protection-keys");
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
    .SetApplicationName("SapDataSync");
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "SapDataSync.Admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddSingleton<DatabaseBootstrapper>();
builder.Services.AddScoped<SapDataQueryService>();
builder.Services.AddScoped<AdminSettingsService>();
builder.Services.AddSingleton<UploadService>();
builder.Services.AddHttpClient<AiPlanningService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(
        Math.Clamp(builder.Configuration.GetValue("AI:TimeoutSeconds", 30), 5, 120));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("ai", limiter =>
    {
        limiter.PermitLimit = Math.Clamp(builder.Configuration.GetValue("AI:RequestsPerMinute", 5), 1, 30);
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
    options.AddFixedWindowLimiter("admin-auth", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(5);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = uploadMaxBytes;
});
builder.Services.AddHttpClient<ManualImportService>(client =>
{
    var baseUrl = builder.Configuration["EtlWorker:BaseUrl"] ?? "http://localhost:8090";
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(15);
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Configuration.GetValue("Database:Initialize", true))
{
    var bootstrapper = app.Services.GetRequiredService<DatabaseBootstrapper>();
    await bootstrapper.InitializeAsync(app.Lifetime.ApplicationStopping);
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", async (IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var connectionString = configuration.GetConnectionString("SqlServer");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Json(
            new { status = "Unhealthy", database = "Connection string is not configured." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN DB_ID(N'SapDataSync') IS NULL THEN 0 ELSE 1 END";
        var databaseExists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;

        return databaseExists
            ? Results.Ok(new { status = "Healthy", database = "SapDataSync" })
            : Results.Json(
                new { status = "Unhealthy", database = "SapDataSync database was not found." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception exception)
    {
        return Results.Json(
            new { status = "Unhealthy", database = exception.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/admin/status", async (
    HttpContext context,
    AdminSettingsService settingsService,
    CancellationToken cancellationToken) =>
    Results.Ok(await settingsService.GetStatusAsync(
        context.User.Identity?.IsAuthenticated == true,
        cancellationToken)));

app.MapPost("/api/admin/setup", async (
    AdminSetupRequest request,
    HttpContext context,
    AdminSettingsService settingsService,
    CancellationToken cancellationToken) =>
{
    if (!HasAdminRequestHeader(context.Request))
    {
        return Results.BadRequest(new ProblemDetails { Title = "Yêu cầu quản trị không hợp lệ." });
    }

    try
    {
        await settingsService.CreateAdminAsync(
            request.Password,
            context.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        await SignInAdminAsync(context);
        return Results.Ok(await settingsService.GetStatusAsync(true, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.Password)] = [exception.Message]
        });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new ProblemDetails
        {
            Title = "Không thể thiết lập quản trị",
            Detail = exception.Message
        });
    }
}).RequireRateLimiting("admin-auth");

app.MapPost("/api/admin/login", async (
    AdminLoginRequest request,
    HttpContext context,
    AdminSettingsService settingsService,
    CancellationToken cancellationToken) =>
{
    if (!HasAdminRequestHeader(context.Request))
    {
        return Results.BadRequest(new ProblemDetails { Title = "Yêu cầu quản trị không hợp lệ." });
    }

    var valid = await settingsService.ValidateAdminPasswordAsync(
        request.Password,
        context.Connection.RemoteIpAddress?.ToString(),
        cancellationToken);
    if (!valid)
    {
        return Results.Json(
            new ProblemDetails
            {
                Title = "Đăng nhập không thành công",
                Detail = "Mật khẩu quản trị không đúng."
            },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    await SignInAdminAsync(context);
    return Results.Ok(await settingsService.GetStatusAsync(true, cancellationToken));
}).RequireRateLimiting("admin-auth");

app.MapPost("/api/admin/logout", async (
    HttpContext context,
    AdminSettingsService settingsService,
    CancellationToken cancellationToken) =>
{
    if (!HasAdminRequestHeader(context.Request))
    {
        return Results.BadRequest(new ProblemDetails { Title = "Yêu cầu quản trị không hợp lệ." });
    }

    await settingsService.WriteLogoutAuditAsync(
        context.Connection.RemoteIpAddress?.ToString(),
        cancellationToken);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { message = "Đã đăng xuất." });
}).RequireAuthorization();

app.MapPut("/api/admin/settings/ai", async (
    AiApiKeyUpdateRequest request,
    HttpContext context,
    AdminSettingsService settingsService,
    CancellationToken cancellationToken) =>
{
    if (!HasAdminRequestHeader(context.Request))
    {
        return Results.BadRequest(new ProblemDetails { Title = "Yêu cầu quản trị không hợp lệ." });
    }

    try
    {
        await settingsService.SaveAiApiKeyAsync(
            request.ApiKey,
            context.User.Identity?.Name ?? "admin",
            context.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        return Results.Ok(await settingsService.GetStatusAsync(true, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.ApiKey)] = [exception.Message]
        });
    }
}).RequireAuthorization();

app.MapDelete("/api/admin/settings/ai", async (
    HttpContext context,
    AdminSettingsService settingsService,
    CancellationToken cancellationToken) =>
{
    if (!HasAdminRequestHeader(context.Request))
    {
        return Results.BadRequest(new ProblemDetails { Title = "Yêu cầu quản trị không hợp lệ." });
    }

    await settingsService.RemoveAiApiKeyAsync(
        context.User.Identity?.Name ?? "admin",
        context.Connection.RemoteIpAddress?.ToString(),
        cancellationToken);
    return Results.Ok(await settingsService.GetStatusAsync(true, cancellationToken));
}).RequireAuthorization();

app.MapPost("/api/admin/settings/ai/test", async (
    AiApiKeyTestRequest request,
    HttpContext context,
    AiPlanningService aiService,
    CancellationToken cancellationToken) =>
{
    if (!HasAdminRequestHeader(context.Request))
    {
        return Results.BadRequest(new ProblemDetails { Title = "Yêu cầu quản trị không hợp lệ." });
    }

    try
    {
        return Results.Ok(await aiService.TestConnectionAsync(request.ApiKey, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new ProblemDetails
        {
            Title = "Thiếu API key",
            Detail = exception.Message
        });
    }
    catch (AiProviderException exception)
    {
        return Results.Problem(
            title: "Không thể xác nhận API key",
            detail: exception.Message,
            statusCode: (int)exception.StatusCode);
    }
    catch (HttpRequestException)
    {
        return Results.Problem(
            title: "Không thể kết nối AI Provider",
            detail: "Kiểm tra kết nối Internet hoặc thử lại sau.",
            statusCode: StatusCodes.Status502BadGateway);
    }
}).RequireAuthorization();

app.MapGet("/api/sap-data", async (
    [AsParameters] SapDataQuery query,
    SapDataQueryService service,
    CancellationToken cancellationToken) =>
{
    var validation = query.Validate();
    return validation is null
        ? Results.Ok(await service.GetSapDataAsync(query, cancellationToken))
        : Results.ValidationProblem(validation);
});

app.MapGet("/api/sap-data/filter-options", async (
    SapDataQueryService service,
    CancellationToken cancellationToken) =>
    Results.Ok(await service.GetFilterOptionsAsync(cancellationToken)));

app.MapGet("/api/sap-data/{id:long}", async (
    long id,
    SapDataQueryService service,
    CancellationToken cancellationToken) =>
{
    var item = await service.GetSapDataDetailAsync(id, cancellationToken);
    return item is null
        ? Results.NotFound(new ProblemDetails
        {
            Title = "Không tìm thấy dữ liệu SAP",
            Detail = $"Không tồn tại bản ghi SAP có ID {id}.",
            Status = StatusCodes.Status404NotFound
        })
        : Results.Ok(item);
});

app.MapGet("/api/ai/status", async (
    AiPlanningService aiService,
    CancellationToken cancellationToken) =>
    Results.Ok(await aiService.GetStatusAsync(cancellationToken)));

app.MapPost("/api/ai/filters", async (
    AiFilterRequest request,
    SapDataQueryService dataService,
    AiPlanningService aiService,
    CancellationToken cancellationToken) =>
{
    var validation = request.Validate();
    if (validation is not null)
    {
        return Results.ValidationProblem(validation);
    }

    try
    {
        var response = await aiService.InterpretFilterAsync(request, cancellationToken);
        var domainValidation = ValidateAiFilterValues(
            response.Query,
            await dataService.GetFilterOptionsAsync(cancellationToken));
        return domainValidation is null
            ? Results.Ok(response)
            : Results.ValidationProblem(domainValidation);
    }
    catch (AiProviderException exception)
    {
        return Results.Problem(
            title: "Không thể tạo bộ lọc AI",
            detail: exception.Message,
            statusCode: (int)exception.StatusCode);
    }
    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem(
            title: "AI phản hồi quá thời gian",
            detail: "Hãy thử lại với câu hỏi ngắn hơn.",
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (HttpRequestException)
    {
        return Results.Problem(
            title: "Không thể kết nối AI Provider",
            detail: "Kiểm tra kết nối Internet, AI_BASE_URL hoặc thử lại sau.",
            statusCode: StatusCodes.Status502BadGateway);
    }
}).RequireRateLimiting("ai");

app.MapPost("/api/ai/plans", async (
    AiPlanRequest request,
    SapDataQueryService dataService,
    AiPlanningService aiService,
    CancellationToken cancellationToken) =>
{
    var validation = request.Validate();
    if (validation is not null)
    {
        return Results.ValidationProblem(validation);
    }

    try
    {
        var data = await dataService.GetSapDataAsync(
            CreateAiQuery(request.Query!, aiService.MaxRecords),
            cancellationToken);
        return Results.Ok(await aiService.GeneratePlanAsync(request, data, cancellationToken));
    }
    catch (AiProviderException exception)
    {
        return Results.Problem(
            title: "Không thể tạo kế hoạch AI",
            detail: exception.Message,
            statusCode: (int)exception.StatusCode);
    }
    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem(
            title: "AI phản hồi quá thời gian",
            detail: "Hãy thử lại với bộ lọc hẹp hơn.",
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (HttpRequestException)
    {
        return Results.Problem(
            title: "Không thể kết nối AI Provider",
            detail: "Kiểm tra kết nối Internet, AI_BASE_URL hoặc thử lại sau.",
            statusCode: StatusCodes.Status502BadGateway);
    }
}).RequireRateLimiting("ai");

app.MapGet("/api/import-logs", async (
    [AsParameters] ImportLogQuery query,
    SapDataQueryService service,
    CancellationToken cancellationToken) =>
{
    var validation = query.Validate();
    return validation is null
        ? Results.Ok(await service.GetImportLogsAsync(query, cancellationToken))
        : Results.ValidationProblem(validation);
});

app.MapGet("/api/import-logs/{id:guid}", async (
    Guid id,
    SapDataQueryService service,
    CancellationToken cancellationToken) =>
{
    var item = await service.GetImportLogDetailAsync(id, cancellationToken);
    return item is null
        ? Results.NotFound(new ProblemDetails
        {
            Title = "Không tìm thấy lịch sử import",
            Detail = $"Không tồn tại lần import có ID {id}.",
            Status = StatusCodes.Status404NotFound
        })
        : Results.Ok(item);
});

app.MapGet("/api/import-logs/{id:guid}/changes", async (
    Guid id,
    [AsParameters] ChangeLogQuery query,
    SapDataQueryService service,
    CancellationToken cancellationToken) =>
{
    var validation = query.Validate();
    return validation is null
        ? Results.Ok(await service.GetImportChangesAsync(id, query, cancellationToken))
        : Results.ValidationProblem(validation);
});

app.MapPost("/api/imports/run", async (
    HttpContext context,
    ManualImportService service,
    CancellationToken cancellationToken) =>
{
    if (!HasAdminRequestHeader(context.Request))
    {
        return Results.BadRequest(new ProblemDetails { Title = "Yêu cầu quản trị không hợp lệ." });
    }

    try
    {
        var result = await service.StartAsync(cancellationToken);
        return Results.Json(result.Status, statusCode: (int)result.StatusCode);
    }
    catch (HttpRequestException)
    {
        return Results.Problem(
            title: "ETL Worker chưa sẵn sàng",
            detail: "Không thể gửi yêu cầu import thủ công. Hãy kiểm tra ETL Worker đang chạy.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireAuthorization();

app.MapGet("/api/imports/status", async (
    ManualImportService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.GetStatusAsync(cancellationToken));
    }
    catch (HttpRequestException)
    {
        return Results.Problem(
            title: "ETL Worker chưa sẵn sàng",
            detail: "Không thể đọc trạng thái import thủ công.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/uploads", async (
    HttpContext context,
    HttpRequest request,
    UploadService uploadService,
    CancellationToken cancellationToken) =>
{
    if (!HasAdminRequestHeader(context.Request))
    {
        return Results.BadRequest(new ProblemDetails { Title = "Yêu cầu quản trị không hợp lệ." });
    }

    if (!uploadService.Enabled)
    {
        return Results.Problem(
            title: "Upload đang bị tắt",
            detail: "Quản trị viên chưa bật chức năng upload file Excel.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    try
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new ProblemDetails
            {
                Title = "Thiếu file upload",
                Detail = "Yêu cầu phải dùng multipart/form-data."
            });
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null)
        {
            return Results.BadRequest(new ProblemDetails
            {
                Title = "Thiếu file upload",
                Detail = "Hãy chọn một file Excel .xlsx."
            });
        }

        return Results.Ok(await uploadService.SaveAsync(file, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new ProblemDetails
        {
            Title = "File upload không hợp lệ",
            Detail = exception.Message
        });
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
    {
        return Results.Problem(
            title: "Không thể lưu file Excel",
            detail: "Hệ thống không ghi được file vào vùng lưu trữ. Hãy khởi động lại hệ thống; nếu lỗi vẫn còn, kiểm tra log Web API.",
            statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireAuthorization();

app.MapFallbackToFile("index.html");

await app.RunAsync();

static bool HasAdminRequestHeader(HttpRequest request) =>
    string.Equals(request.Headers["X-SapDataSync-Admin"], "1", StringComparison.Ordinal);

static Task SignInAdminAsync(HttpContext context)
{
    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Administrator")],
        CookieAuthenticationDefaults.AuthenticationScheme);
    return context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
        });
}

static SapDataQuery CreateAiQuery(SapDataQuery source, int maxRecords) => new()
{
    Page = 1,
    PageSize = maxRecords,
    Search = source.Search,
    Product = source.Product,
    SalesOrganization = source.SalesOrganization,
    BusinessScenario = source.BusinessScenario,
    SiStatus = source.SiStatus,
    SalesOffice = source.SalesOffice,
    PlantCode = source.PlantCode,
    SiId = source.SiId,
    Customer = source.Customer,
    OilSc = source.OilSc,
    OilSo = source.OilSo,
    OilPo = source.OilPo,
    CreatedFrom = source.CreatedFrom,
    CreatedTo = source.CreatedTo,
    SortBy = source.SortBy,
    SortDirection = source.SortDirection
};

static Dictionary<string, string[]>? ValidateAiFilterValues(SapDataQuery query, FilterOptions options)
{
    var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    ValidateKnownValue(nameof(query.Product), query.Product, options.Products);
    ValidateKnownValue(nameof(query.SalesOrganization), query.SalesOrganization, options.SalesOrganizations);
    ValidateKnownValue(nameof(query.SiStatus), query.SiStatus, options.SiStatuses);
    ValidateKnownValue(nameof(query.SalesOffice), query.SalesOffice, options.SalesOffices);
    ValidateKnownValue(nameof(query.PlantCode), query.PlantCode, options.PlantCodes);

    var scenarioCodes = new HashSet<string>(["PDO", "PWS", "SDS", "SWS"], StringComparer.OrdinalIgnoreCase);
    foreach (var scenario in (query.BusinessScenario ?? string.Empty)
                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!scenarioCodes.Contains(scenario) &&
            !options.BusinessScenarios.Any(value => value.Equals(scenario, StringComparison.OrdinalIgnoreCase)))
        {
            errors[nameof(query.BusinessScenario)] = ["AI đề xuất Business Scenario không tồn tại trong dữ liệu."];
            break;
        }
    }

    return errors.Count == 0 ? null : errors;

    void ValidateKnownValue(string field, string? value, IReadOnlyList<string> allowed)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            !allowed.Any(item => item.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            errors[field] = [$"AI đề xuất {field} không tồn tại trong dữ liệu."];
        }
    }
}
