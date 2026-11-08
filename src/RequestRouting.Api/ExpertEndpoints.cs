using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using RequestRouting.Application;
using RequestRouting.Infrastructure.Persistence;

namespace RequestRouting.Api;

public sealed record CreateExpertBody(
    [Required, StringLength(AppDbContext.NameMaxLength, MinimumLength = 1)] string Name,
    [Range(1, int.MaxValue)] int Capacity,
    [Required, MinLength(1), TopicCodes] string[] Topics,
    bool IsActive = true);

public sealed record UpdateExpertBody(
    [Range(1, int.MaxValue)] int? Capacity,
    bool? IsActive,
    [MinLength(1), TopicCodes] string[]? Topics);

public static class ExpertEndpoints
{
    public static void MapExpertEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/experts").WithTags("Experts");

        group.MapPost("/", async Task<Created<ExpertView>> (CreateExpertBody body, ExpertService service, CancellationToken ct) =>
        {
            var expert = await service.CreateAsync(
                new CreateExpertCommand(body.Name, body.Capacity, body.Topics, body.IsActive), ct);
            return TypedResults.Created($"/api/experts/{expert.Id}", expert);
        });

        group.MapGet("/{id:int}", async Task<Results<Ok<ExpertView>, NotFound>> (int id, ExpertService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } expert ? TypedResults.Ok(expert) : TypedResults.NotFound());

        group.MapPatch("/{id:int}", async Task<Results<Ok<ExpertView>, NotFound>> (int id, UpdateExpertBody body, ExpertService service, CancellationToken ct) =>
            await service.UpdateAsync(id, new UpdateExpertCommand(body.Capacity, body.IsActive, body.Topics), ct) is { } expert
                ? TypedResults.Ok(expert)
                : TypedResults.NotFound());
    }
}
