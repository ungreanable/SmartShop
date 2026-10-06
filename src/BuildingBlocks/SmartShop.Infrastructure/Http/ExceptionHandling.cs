using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartShop.SharedKernel;

namespace SmartShop.Infrastructure.Http;

/// <summary>Maps domain exceptions to RFC 9457 problem details with a stable <c>code</c> extension for clients.</summary>
internal sealed class SmartShopExceptionHandler(IProblemDetailsService problemDetails, ILogger<SmartShopExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, title) = exception switch
        {
            ConflictException e => (StatusCodes.Status409Conflict, e.Code, e.Message),
            TooLargeException e => (StatusCodes.Status413PayloadTooLarge, e.Code, e.Message),
            UnavailableException e => (StatusCodes.Status503ServiceUnavailable, e.Code, e.Message),
            DomainException e => (StatusCodes.Status400BadRequest, e.Code, e.Message),
            NotFoundException e => (StatusCodes.Status404NotFound, "not_found", e.Message),
            ForbiddenException e => (StatusCodes.Status403Forbidden, "forbidden", e.Message),
            UnauthorizedAccessException e => (StatusCodes.Status401Unauthorized, "unauthorized", e.Message),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "concurrency",
                "The data was changed by someone else. Please refresh and try again."),
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } e => (e.StatusCode, "media_too_large", e.Message),
            BadHttpRequestException e => (e.StatusCode, "bad_request", e.Message),
            _ => (0, "", ""),
        };

        if (status == 0)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
            return false;
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Extensions = { ["code"] = code },
            },
        });
    }
}
