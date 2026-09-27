using Microsoft.AspNetCore.Mvc;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class ColesExtensionEndpoints
{
    public static void MapColesExtensionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/coles-worker").RequireAuthorization("ColesWorkerOnly");
        group.AddEndpointFilter(async (context, next) => {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("/tasks", async (ColesExtensionTaskService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)));
        group.MapPost("/tasks/{id:long}/claim", async (long id, ColesWorkerRequest request, ColesExtensionTaskService service, CancellationToken ct) =>
        {
            if (!ColesExtensionTaskService.ValidWorker(request.WorkerId)) return Results.BadRequest();
            var claim = await service.ClaimAsync(id, request.WorkerId, ct);
            return claim is null ? Results.Conflict() : Results.Ok(claim);
        });
        group.MapPost("/tasks/{id:long}/result", async (long id, ColesTaskSubmission result, ColesExtensionTaskService service, CancellationToken ct) =>
        {
            var error = await service.SubmitAsync(id, result, ct);
            return error is null ? Results.Ok(new { saved = true }) : error is "claim_lost" or "submission_conflict"
                ? Results.Conflict(new { error }) : Results.BadRequest(new { error });
        }).WithMetadata(new RequestSizeLimitAttribute(2500000));
        group.MapPost("/tasks/{id:long}/retry", async (long id, ColesExtensionTaskService service, CancellationToken ct) =>
            await service.RetryAsync(id, ct) ? Results.Ok() : Results.Conflict());
    }
}
