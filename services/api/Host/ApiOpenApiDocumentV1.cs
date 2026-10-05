using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;

namespace QuoteEngine.Api;

internal static class ApiOpenApiDocumentV1
{
    public const string DocumentPath = "/openapi/v1.json";
    public const string MediaType = "application/vnd.oai.openapi+json";

    private static readonly Regex RouteConstraintPattern = new(
        @"\{([^}:]+)(?::[^}]+)?\}",
        RegexOptions.CultureInvariant);

    public static object RfqFileUpload { get; } = new ApiOperationMetadata(
        "post",
        "UploadRfqFile",
        "Upload an immutable RFQ file version.",
        "RFQ Files",
        [
            new("tenantId", "string", "uuid"),
            new("quoteRequestId", "string", "uuid"),
            new("logicalKey", "string", null)
        ],
        new("multipart/form-data", "file"),
        [
            Json(200, "RFQ file version metadata."),
            Problem(400, "Invalid RFQ file request."),
            Problem(404, "RFQ was not found for the tenant."),
            Problem(413, "RFQ file exceeds the configured upload limit."),
            Problem(415, "RFQ file MIME type is not allowed."),
            Problem(503, "RFQ file storage policy is not configured.")
        ]);

    public static object RfqFileDownload { get; } = new ApiOperationMetadata(
        "get",
        "DownloadRfqFileVersion",
        "Download one immutable RFQ file version.",
        "RFQ Files",
        [
            new("tenantId", "string", "uuid"),
            new("quoteRequestId", "string", "uuid"),
            new("logicalKey", "string", null),
            new("versionNo", "integer", "int32")
        ],
        null,
        [
            Binary(200, "RFQ file bytes."),
            Problem(404, "RFQ file version was not found."),
            Problem(500, "RFQ metadata exists but the stored object is missing."),
            Problem(503, "RFQ file storage policy is not configured.")
        ]);

    public static object Health { get; } = new ApiOperationMetadata(
        "get",
        "GetHealth",
        "Return process health.",
        "System",
        [],
        null,
        [Json(200, "Process is healthy.")]);

    public static object Build(EndpointDataSource endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var paths = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            var metadata = endpoint.Metadata.GetMetadata<ApiOperationMetadata>();
            if (metadata is null || string.IsNullOrWhiteSpace(endpoint.RoutePattern.RawText)) continue;

            var route = "/" + endpoint.RoutePattern.RawText.TrimStart('/');
            route = RouteConstraintPattern.Replace(route, "{$1}");

            if (!paths.TryGetValue(route, out var existing))
            {
                existing = new SortedDictionary<string, object>(StringComparer.Ordinal);
                paths.Add(route, existing);
            }

            ((SortedDictionary<string, object>)existing).Add(
                metadata.Method.ToLowerInvariant(),
                BuildOperation(metadata));
        }

        return new Dictionary<string, object?>
        {
            ["openapi"] = "3.0.3",
            ["info"] = new Dictionary<string, object?>
            {
                ["title"] = "AI Manufacturing Quote Engine API",
                ["version"] = "v1"
            },
            ["paths"] = paths
        };
    }

    private static object BuildOperation(ApiOperationMetadata metadata)
    {
        var operation = new Dictionary<string, object?>
        {
            ["operationId"] = metadata.OperationId,
            ["summary"] = metadata.Summary,
            ["tags"] = new[] { metadata.Tag },
            ["parameters"] = metadata.Parameters.Select(BuildParameter).ToArray(),
            ["responses"] = BuildResponses(metadata.Responses)
        };
        if (metadata.RequestBody is not null)
            operation["requestBody"] = BuildRequestBody(metadata.RequestBody);
        return operation;
    }

    private static object BuildParameter(ApiParameterMetadata parameter)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = parameter.Type
        };
        if (parameter.Format is not null) schema["format"] = parameter.Format;

        return new Dictionary<string, object?>
        {
            ["name"] = parameter.Name,
            ["in"] = "path",
            ["required"] = true,
            ["schema"] = schema
        };
    }

    private static object BuildRequestBody(ApiRequestBodyMetadata requestBody) =>
        new Dictionary<string, object?>
        {
            ["required"] = true,
            ["content"] = new Dictionary<string, object?>
            {
                [requestBody.ContentType] = new Dictionary<string, object?>
                {
                    ["schema"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["required"] = new[] { requestBody.FileFieldName },
                        ["properties"] = new Dictionary<string, object?>
                        {
                            [requestBody.FileFieldName] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["format"] = "binary"
                            }
                        }
                    }
                }
            }
        };

    private static object BuildResponses(IReadOnlyList<ApiResponseMetadata> responses)
    {
        var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var response in responses.OrderBy(item => item.StatusCode))
        {
            var value = new Dictionary<string, object?>
            {
                ["description"] = response.Description
            };
            if (response.ContentType is not null)
            {
                var schema = new Dictionary<string, object?>
                {
                    ["type"] = response.SchemaType ?? "object"
                };
                if (response.SchemaFormat is not null) schema["format"] = response.SchemaFormat;
                value["content"] = new Dictionary<string, object?>
                {
                    [response.ContentType] = new Dictionary<string, object?>
                    {
                        ["schema"] = schema
                    }
                };
            }
            result[response.StatusCode.ToString(CultureInfo.InvariantCulture)] = value;
        }
        return result;
    }

    private static ApiResponseMetadata Json(int statusCode, string description) =>
        new(statusCode, description, "application/json", "object", null);

    private static ApiResponseMetadata Problem(int statusCode, string description) =>
        new(statusCode, description, "application/problem+json", "object", null);

    private static ApiResponseMetadata Binary(int statusCode, string description) =>
        new(statusCode, description, "application/octet-stream", "string", "binary");

    private sealed record ApiOperationMetadata(
        string Method,
        string OperationId,
        string Summary,
        string Tag,
        IReadOnlyList<ApiParameterMetadata> Parameters,
        ApiRequestBodyMetadata? RequestBody,
        IReadOnlyList<ApiResponseMetadata> Responses);

    private sealed record ApiParameterMetadata(string Name, string Type, string? Format);

    private sealed record ApiRequestBodyMetadata(string ContentType, string FileFieldName);

    private sealed record ApiResponseMetadata(
        int StatusCode,
        string Description,
        string? ContentType,
        string? SchemaType,
        string? SchemaFormat);
}
