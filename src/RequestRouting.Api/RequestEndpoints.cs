using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using RequestRouting.Application;
using RequestRouting.Domain;
using RequestRouting.Domain.Requests;
using RequestRouting.Infrastructure.Persistence;

namespace RequestRouting.Api;

public sealed record SubmitRequestBody(
    [Required, StringLength(AppDbContext.RequestKeyMaxLength, MinimumLength = 1)] string RequestKey,
    [Required, RegularExpression(TopicCode.Pattern), StringLength(TopicCode.MaxLength)] string Topic,
    [Required, StringLength(AppDbContext.NameMaxLength, MinimumLength = 1)] string Title,
    [Required] RequestPriority? Priority);

public static class RequestEndpoints
{
    private const int MaxPageSize = 200;

    public static void MapRequestEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/requests").WithTags("Requests");

        // 201 for a new request, 200 when RequestKey identifies one that already exists.
        group.MapPost("/", async Task<Results<Created<RequestView>, Ok<RequestView>>> (SubmitRequestBody body, RequestService service, CancellationToken ct) =>
        {
            var result = await service.SubmitAsync(
                new SubmitRequestCommand(body.RequestKey, body.Topic, body.Title, body.Priority!.Value), ct);

            return result.Created
                ? TypedResults.Created($"/api/requests/{result.Request.Id}", result.Request)
                : TypedResults.Ok(result.Request);
        });

        group.MapGet("/{id:long}", async Task<Results<Ok<RequestView>, NotFound>> (long id, RequestService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } request ? TypedResults.Ok(request) : TypedResults.NotFound());

        group.MapPost("/{id:long}/close", async Task<Results<Ok<RequestView>, NotFound>> (long id, RequestService service, CancellationToken ct) =>
            await service.CloseAsync(id, ct) is { } request ? TypedResults.Ok(request) : TypedResults.NotFound());

        // The waiting queue, in the order it will be served.
        group.MapGet("/pending", async Task<Ok<IReadOnlyList<RequestView>>> (RequestService service, CancellationToken ct, [Range(1, MaxPageSize)] int take = 50) =>
            TypedResults.Ok(await service.ListPendingAsync(take, ct)));
    }
}
