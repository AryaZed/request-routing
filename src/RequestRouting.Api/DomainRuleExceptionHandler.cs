using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using RequestRouting.Domain;

namespace RequestRouting.Api;

/// <summary>A broken business rule is the client's conflict with current state, reported as 409.</summary>
public sealed class DomainRuleExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not DomainRuleException rule)
        {
            return false;
        }

        context.Response.StatusCode = StatusCodes.Status409Conflict;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "The request conflicts with a business rule.",
                Detail = rule.Message,
                Type = $"urn:request-routing:{rule.Code}",
            },
        });
    }
}
