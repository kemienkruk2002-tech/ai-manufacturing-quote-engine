using Microsoft.AspNetCore.Diagnostics;
using QuoteEngine.Application;
using QuoteEngine.Domain.Calculation;

namespace QuoteEngine.Api;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        var extensions = new Dictionary<string, object?>();
        int statusCode;
        string title;
        string code;
        LogLevel level;

        switch (exception)
        {
            case DomainValidationException validation:
                statusCode = StatusCodes.Status400BadRequest;
                title = "Request validation failed.";
                code = "DOMAIN_VALIDATION_FAILED";
                level = LogLevel.Warning;
                extensions["errors"] = validation.Errors.Select(error => new
                {
                    code = error.Code,
                    message = error.Message,
                    operation_no = error.OperationNo
                }).ToArray();
                break;

            case MissingSnapshotInputException missing:
                statusCode = StatusCodes.Status422UnprocessableEntity;
                title = "Required quote data is missing or incomplete.";
                code = missing.Code;
                level = LogLevel.Warning;
                break;

            case FileObjectHashMismatchException:
                statusCode = StatusCodes.Status409Conflict;
                title = "RFQ file content failed integrity verification.";
                code = "RFQ_FILE_HASH_MISMATCH";
                level = LogLevel.Warning;
                break;

            case TenantContextUnavailableException:
                statusCode = StatusCodes.Status401Unauthorized;
                title = "Authenticated tenant context is required.";
                code = "TENANT_CONTEXT_REQUIRED";
                level = LogLevel.Warning;
                break;

            case TenantResourceMismatchException:
                statusCode = StatusCodes.Status404NotFound;
                title = "The requested resource was not found.";
                code = "TENANT_RESOURCE_NOT_FOUND";
                level = LogLevel.Warning;
                break;

            default:
                statusCode = StatusCodes.Status500InternalServerError;
                title = "An unexpected error occurred.";
                code = "INTERNAL_SERVER_ERROR";
                level = LogLevel.Error;
                break;
        }

        extensions["code"] = code;
        logger.Log(level,
            "api_exception_handled status_code={status_code} code={code} correlation_id={correlation_id} exception_type={exception_type}",
            statusCode, code, httpContext.TraceIdentifier, exception.GetType().Name);

        await Results.Problem(statusCode: statusCode, title: title, extensions: extensions)
            .ExecuteAsync(httpContext);
        return true;
    }
}
