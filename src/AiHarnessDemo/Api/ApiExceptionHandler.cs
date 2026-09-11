using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AiHarnessDemo.Api;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var statusCode = exception switch
        {
            DemoRuntimeException runtimeException => runtimeException.StatusCode,
            NewWorkAdmissionException => StatusCodes.Status503ServiceUnavailable,
            IntakeAttemptException => StatusCodes.Status409Conflict,
            DeliveryReadinessConflictException => StatusCodes.Status409Conflict,
            DeliveryReadinessContractException => StatusCodes.Status409Conflict,
            FlowLifecycleException => StatusCodes.Status409Conflict,
            BadHttpRequestException or JsonException or FormatException =>
                StatusCodes.Status400BadRequest,
            ArgumentException => StatusCodes.Status400BadRequest,
            DirectoryNotFoundException or FileNotFoundException => StatusCodes.Status404NotFound,
            KeyNotFoundException => StatusCodes.Status404NotFound,
            UnauthorizedAccessException => StatusCodes.Status403Forbidden,
            DbUpdateConcurrencyException => StatusCodes.Status409Conflict,
            InvalidOperationException => StatusCodes.Status409Conflict,
            TimeoutException => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status500InternalServerError
        };

        if (statusCode >= 500)
        {
            logger.LogError(exception, "Unhandled request failure at {Path}", httpContext.Request.Path);
        }
        else
        {
            logger.LogWarning(
                "Request rejected at {Path}: {Message}",
                httpContext.Request.Path,
                exception.Message);
        }

        var title = statusCode switch
        {
            StatusCodes.Status400BadRequest => "The request is invalid.",
            StatusCodes.Status404NotFound => "The requested resource was not found.",
            StatusCodes.Status409Conflict => "The request conflicts with the current flow state.",
            StatusCodes.Status503ServiceUnavailable => "New work is temporarily unavailable.",
            StatusCodes.Status504GatewayTimeout => "The operation timed out.",
            _ => "The harness could not complete the operation."
        };
        var detail = statusCode >= 500 &&
                     exception is not NewWorkAdmissionException and not TimeoutException
            ? "An unexpected server error occurred."
            : exception.Message;
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path
        };
        if (exception is NewWorkAdmissionException admission)
        {
            problem.Extensions["failures"] = admission.Failures;
        }
        if (exception is IntakeAttemptException intake)
        {
            problem.Extensions["flowId"] = intake.FlowId;
            problem.Extensions["flowStatus"] =
                intake.FlowStatus.ToString();
            problem.Extensions["retryMessage"] =
                intake.RetryMessage;
        }
        if (exception is DeliveryReadinessConflictException readiness)
        {
            // Stable machine-readable codes let a stale client refresh the authoritative readiness
            // binding instead of retrying an action the server will never accept.
            problem.Title = "The request conflicts with the current Delivery readiness.";
            problem.Extensions["code"] = readiness.Code;
            problem.Extensions["readinessState"] = readiness.State?.ToString();
            problem.Extensions["readinessRevision"] = readiness.Revision;
            problem.Extensions["readinessContractHash"] = readiness.ContractHash;
        }
        if (exception is DeliveryReadinessContractException contract)
        {
            problem.Extensions["code"] = DeliveryReadinessConflicts.ContractInvalid;
            problem.Extensions["errors"] = contract.Errors;
        }
        if (exception is DemoRuntimeException demo)
        {
            problem.Title = "The live demo operation could not be completed.";
            problem.Extensions["code"] = demo.Code;
        }
        if (LocalRequestGuard.IsExactDemoProxyRoute(httpContext.Request.Path))
        {
            DemoReverseProxy.ApplyIsolationHeaders(httpContext.Response);
        }
        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(
            problem,
            options: null,
            contentType: "application/problem+json",
            cancellationToken);
        return true;
    }
}
