using Microsoft.AspNetCore.Diagnostics;
using Roster.Platform;

namespace Roster.Api.Http;

/// <summary>
/// Turns the exceptions the platform uses to say "no" into the right HTTP status, as RFC 9457 problem details with the
/// platform's message (written for people) as the detail. Anything else is a real error and stays a 500.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term>403</term><description><see cref="UnauthorizedAccessException"/>: someone else's assignment, retro or finding.</description></item>
/// <item><term>404</term><description><see cref="KeyNotFoundException"/>: no such finding or card.</description></item>
/// <item><term>400</term><description><see cref="ArgumentException"/> and <see cref="NotSupportedException"/>: a bad request,
/// such as a rejection without a reason or an unknown scenario.</description></item>
/// <item><term>409</term><description><see cref="ConflictException"/>: valid, but not now (the review is still running).</description></item>
/// </list>
/// </remarks>
internal sealed class PlatformExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        (int status, string title) = exception switch
        {
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Not allowed"),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            ArgumentException or NotSupportedException => (StatusCodes.Status400BadRequest, "Invalid request"),
            ConflictException => (StatusCodes.Status409Conflict, "Not possible right now"),
            _ => (0, ""),
        };

        if (status == 0)
        {
            return false; // Not ours: the default handler logs it and answers 500.
        }

        httpContext.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = status, Title = title, Detail = exception.Message },
        });
    }
}
