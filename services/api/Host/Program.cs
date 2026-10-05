using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Api;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, ClaimsTenantContext>();

var defaultAuthenticationScheme = builder.Environment.IsDevelopment()
    ? ApiAuthenticationSchemes.Development
    : ApiAuthenticationSchemes.Closed;
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = defaultAuthenticationScheme;
        options.DefaultChallengeScheme = defaultAuthenticationScheme;
    })
    .AddScheme<AuthenticationSchemeOptions, ClosedAuthenticationHandler>(
        ApiAuthenticationSchemes.Closed, _ => { })
    .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
        ApiAuthenticationSchemes.Development, _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(ApiAuthorizationPolicies.TenantRfqAccess, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            var claims = context.User.FindAll(ClaimsTenantContext.TenantClaimType).ToArray();
            return claims.Length == 1
                && Guid.TryParse(claims[0].Value, out var tenantId)
                && tenantId != Guid.Empty;
        });
    });
});
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        var statusCode = context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode;
        context.ProblemDetails.Status = statusCode;
        context.ProblemDetails.Extensions["correlation_id"] = context.HttpContext.TraceIdentifier;
        if (!context.ProblemDetails.Extensions.ContainsKey("code"))
        {
            context.ProblemDetails.Extensions["code"] = statusCode switch
            {
                StatusCodes.Status400BadRequest => "BAD_REQUEST",
                StatusCodes.Status401Unauthorized => "UNAUTHORIZED",
                StatusCodes.Status403Forbidden => "FORBIDDEN",
                StatusCodes.Status404NotFound => "NOT_FOUND",
                StatusCodes.Status405MethodNotAllowed => "METHOD_NOT_ALLOWED",
                StatusCodes.Status409Conflict => "CONFLICT",
                StatusCodes.Status413PayloadTooLarge => "PAYLOAD_TOO_LARGE",
                StatusCodes.Status415UnsupportedMediaType => "UNSUPPORTED_MEDIA_TYPE",
                StatusCodes.Status422UnprocessableEntity => "UNPROCESSABLE_ENTITY",
                StatusCodes.Status503ServiceUnavailable => "SERVICE_UNAVAILABLE",
                _ when statusCode >= 500 => "INTERNAL_SERVER_ERROR",
                _ => "HTTP_ERROR"
            };
        }
    };
});
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSingleton(sp => NpgsqlDataSource.Create(
    sp.GetRequiredService<IConfiguration>().GetConnectionString("QuoteEngine")
    ?? throw new InvalidOperationException("Set ConnectionStrings__QuoteEngine to the PostgreSQL connection string.")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<DatabaseMigrator>();
builder.Services.AddScoped<SnapshotInputRepository>();
builder.Services.AddScoped<ISnapshotInputRepository>(sp => sp.GetRequiredService<SnapshotInputRepository>());
builder.Services.AddScoped<IPartRepository>(sp => sp.GetRequiredService<SnapshotInputRepository>());
builder.Services.AddScoped<IRouteRepository>(sp => sp.GetRequiredService<SnapshotInputRepository>());
builder.Services.AddScoped<IMachineRateRepository>(sp => sp.GetRequiredService<SnapshotInputRepository>());
builder.Services.AddScoped<IQuoteSnapshotRepository, QuoteSnapshotRepository>();
builder.Services.AddScoped<ICalculationRunRepository, CalculationRunRepository>();
builder.Services.AddScoped<IRfqFileRepository, RfqFileRepository>();
builder.Services.AddScoped<CalculationService>();
var app = builder.Build();

var migrate = args.Contains("--migrate", StringComparer.Ordinal);
var seed = args.Contains("--seed-golden", StringComparer.Ordinal);
var calculate = args.Contains("--calculate-golden", StringComparer.Ordinal);
if ((seed || calculate) && !app.Environment.IsDevelopment())
    throw new InvalidOperationException("Golden seed and calculation commands are enabled only in Development.");
if (migrate || seed || calculate)
{
    await using var scope = app.Services.CreateAsyncScope();
    var migrator = scope.ServiceProvider.GetRequiredService<DatabaseMigrator>();
    if (migrate) await migrator.MigrateAsync();
    if (seed) await migrator.SeedGoldenAsync();
    if (calculate)
    {
        var response = await scope.ServiceProvider.GetRequiredService<CalculationService>().CalculateAsync(GoldenCase.CostRequest, "golden-command");
        Console.WriteLine(JsonSerializer.Serialize(GoldenCase.DebugResponse(response), new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        }));
        if (response.Status != CalculationStatus.Success) Environment.ExitCode = 2;
    }
    await app.DisposeAsync();
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment() && builder.Configuration.GetValue("Dev:GoldenEnabled", true))
{
    app.MapGet("/api/dev/golden/w07044", async (CalculationService service, HttpContext http, CancellationToken token) =>
    {
        try
        {
            var result = await service.CalculateAsync(GoldenCase.CostRequest, http.TraceIdentifier, token);
            return Results.Json(GoldenCase.DebugResponse(result), statusCode: result.Status == CalculationStatus.Success ? 200 : 422);
        }
        catch (MissingSnapshotInputException error)
        {
            return Results.Json(new { status = "Blocked", errorCode = error.Code, error.Message, correlationId = http.TraceIdentifier }, statusCode: 422);
        }
        catch (DomainValidationException error)
        {
            return Results.Json(new { status = "ValidationError", error.Errors, correlationId = http.TraceIdentifier }, statusCode: 400);
        }
    });
}

var tenantApi = app.MapGroup("/api/tenants/{tenantId:guid}")
    .RequireAuthorization(ApiAuthorizationPolicies.TenantRfqAccess);

tenantApi.MapPost("/rfqs/{quoteRequestId:guid}/files/{logicalKey}",
    async (Guid tenantId, Guid quoteRequestId, string logicalKey, HttpRequest request,
        ITenantContext tenantContext, IRfqFileRepository repository, IConfiguration configuration, CancellationToken token) =>
    {
        var trustedTenantId = tenantContext.RequireRouteTenant(tenantId);
        if (!TryReadRfqFilePolicy(configuration, out var policy))
            return RfqFileProblem(503, "RFQ_FILE_POLICY_NOT_CONFIGURED", "RFQ file policy is not configured.");
        if (!await repository.RequestExistsAsync(trustedTenantId, quoteRequestId, token)) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(logicalKey) || logicalKey.Any(char.IsControl) || !request.HasFormContentType)
            return RfqFileProblem(400, "RFQ_FILE_REQUEST_INVALID", "A valid logical key and multipart file are required.");

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(token);
        }
        catch (InvalidDataException)
        {
            return RfqFileProblem(400, "RFQ_FILE_REQUEST_INVALID", "The multipart form is invalid.");
        }
        if (form.Count != 0 || form.Files.Count != 1 || form.Files[0].Name != "file")
            return RfqFileProblem(400, "RFQ_FILE_REQUEST_INVALID", "Exactly one multipart field named file is required.");

        var file = form.Files[0];
        if (string.IsNullOrWhiteSpace(file.FileName) || file.FileName.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(file.ContentType))
            return RfqFileProblem(400, "RFQ_FILE_REQUEST_INVALID", "File name and MIME type are required.");
        if (file.Length > policy.MaxUploadBytes)
            return RfqFileProblem(413, "RFQ_FILE_TOO_LARGE", "The RFQ file exceeds the configured upload limit.");
        if (!policy.AllowedMimeTypes.Contains(file.ContentType))
            return RfqFileProblem(415, "RFQ_FILE_MIME_NOT_ALLOWED", "The RFQ file MIME type is not allowed.");

        string sha256;
        await using (var content = file.OpenReadStream())
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(content, token)).ToLowerInvariant();

        IFileObjectStore objectStore = new LocalFileObjectStore(policy.StorageRoot);
        FileObjectPutResult stored;
        await using (var content = file.OpenReadStream())
            stored = await objectStore.PutAsync(content, sha256, token);

        var version = await repository.GetOrCreateVersionAsync(new(trustedTenantId, quoteRequestId, logicalKey,
            file.FileName, file.ContentType, stored.ByteSize, stored.Sha256), token);
        return Results.Ok(version);
    })
    .WithName("UploadRfqFile")
    .WithMetadata(ApiOpenApiDocumentV1.RfqFileUpload);

tenantApi.MapGet("/rfqs/{quoteRequestId:guid}/files/{logicalKey}/versions/{versionNo:int}",
    async (Guid tenantId, Guid quoteRequestId, string logicalKey, int versionNo,
        ITenantContext tenantContext, IRfqFileRepository repository, IConfiguration configuration, CancellationToken token) =>
    {
        var trustedTenantId = tenantContext.RequireRouteTenant(tenantId);
        if (!TryReadRfqFilePolicy(configuration, out var policy))
            return RfqFileProblem(503, "RFQ_FILE_POLICY_NOT_CONFIGURED", "RFQ file policy is not configured.");
        var version = await repository.FindVersionAsync(trustedTenantId, quoteRequestId, logicalKey, versionNo, token);
        if (version is null) return Results.NotFound();

        IFileObjectStore objectStore = new LocalFileObjectStore(policy.StorageRoot);
        var content = await objectStore.OpenReadAsync(version.Sha256, token);
        if (content is null)
            return RfqFileProblem(500, "RFQ_FILE_OBJECT_MISSING", "RFQ file metadata exists but its object is missing.");
        return Results.File(content, version.MimeType ?? "application/octet-stream", version.OriginalFileName);
    })
    .WithName("DownloadRfqFileVersion")
    .WithMetadata(ApiOpenApiDocumentV1.RfqFileDownload);

app.MapGet(ApiOpenApiDocumentV1.DocumentPath, (EndpointDataSource endpoints) =>
        Results.Json(ApiOpenApiDocumentV1.Build(endpoints), contentType: ApiOpenApiDocumentV1.MediaType))
    .WithName("GetOpenApiV1");

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("GetHealth")
    .WithMetadata(ApiOpenApiDocumentV1.Health);
await app.RunAsync();

static bool TryReadRfqFilePolicy(IConfiguration configuration, out RfqFilePolicy policy)
{
    policy = null!;
    var storageRoot = configuration["RfqFiles:StorageRoot"];
    if (string.IsNullOrWhiteSpace(storageRoot)
        || !long.TryParse(configuration["RfqFiles:MaxUploadBytes"], out var maxUploadBytes)
        || maxUploadBytes <= 0) return false;
    var allowed = configuration.GetSection("RfqFiles:AllowedMimeTypes").GetChildren()
        .Select(item => item.Value)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (allowed.Count == 0) return false;
    try
    {
        policy = new(Path.GetFullPath(storageRoot), maxUploadBytes, allowed);
        return true;
    }
    catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
    {
        return false;
    }
}

static IResult RfqFileProblem(int statusCode, string code, string title) =>
    Results.Problem(statusCode: statusCode, title: title,
        extensions: new Dictionary<string, object?> { ["code"] = code });

public static class GoldenCase
{
    public static readonly SnapshotRequest Request = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"),
        Guid.Parse("90000000-0000-0000-0000-000000000001"),
        "W07044", "1", "rates-v1",
        new RuleVersions(TimeEngineV1.Version, StockEngineV1.Version, CanonicalSnapshotSerializer.SchemaVersion));

    public static readonly SnapshotRequest CostRequest = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"),
        Guid.Parse("90000000-0000-0000-0000-000000000001"),
        "W07044", "1", "w07044-koszty-v1",
        new RuleVersions(TimeEngineV1.Version, StockEngineV1.Version, CanonicalCostSnapshotSerializer.SchemaVersion));

    public static object DebugResponse(CalculationResponse response) => new
    {
        response.Status,
        part = response.Snapshot.PartRevision,
        material = response.Snapshot.Material,
        stock = response.Snapshot.Stock,
        route = response.Snapshot.Route,
        calculatedTimeResult = response.Time,
        calculatedStockResult = response.Stock,
        calculatedCostResult = response.Cost,
        calculatedCostDisplay = response.Cost is { Status: CalculationStatus.Success } cost
            ? CostPresentation.Project(cost)
            : null,
        machineRates = response.Snapshot.MachineRates,
        snapshot_hash = response.SnapshotHash,
        calculation_hash = response.CalculationHash,
        engine_version = response.EngineVersion
    };
}

public partial class Program;

internal sealed record RfqFilePolicy(string StorageRoot, long MaxUploadBytes,
    IReadOnlySet<string> AllowedMimeTypes);
