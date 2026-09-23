using Assessment.Api.Domain;
using Assessment.Api.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Assessment.Api.Infrastructure;

/// <summary>
/// Maps domain and persistence exceptions to ProblemDetails. Anything unmapped stays a generic 500
/// with no internals in the body.
/// </summary>
public sealed class ProblemDetailsExceptionHandler(IProblemDetailsService problemDetails, ILogger<ProblemDetailsExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        (int Status, string Title)? mapped = exception switch
        {
            InvalidTransitionException or StaleRevisionException => (StatusCodes.Status409Conflict, exception.Message),
            DbUpdateConcurrencyException =>
                (StatusCodes.Status409Conflict, "The request was changed by someone else. Reload it and try again."),
            DomainException => (StatusCodes.Status400BadRequest, exception.Message),
            // Malformed JSON / unbindable values: a client error. The parser's message is not echoed back.
            BadHttpRequestException bad => (bad.StatusCode, "The request body or parameters are malformed."),
            _ => null,
        };

        if (exception is TenantIsolationException)
            logger.LogCritical(exception, "Tenant isolation guard blocked a write"); // a bug: must never happen

        if (mapped is null) return false;

        http.Response.StatusCode = mapped.Value.Status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = { Status = mapped.Value.Status, Title = mapped.Value.Title },
            Exception = exception,
        });
    }
}
