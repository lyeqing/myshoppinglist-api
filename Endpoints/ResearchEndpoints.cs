using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class ResearchEndpoints
{
    public static void MapResearchEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/research").RequireAuthorization();
        group.MapGet("/instructions", () => Results.Ok(ResearchWorkflow.Instructions()));
        group.MapGet("", (HttpContext c, ResearchService s, CancellationToken ct) => Handle(c, () => s.JobsAsync(Account(c), ct)));
        group.MapGet("/{id:guid}", (Guid id, HttpContext c, ResearchService s, CancellationToken ct) => Handle(c, () => s.ReviewAsync(Account(c), id, ct)));
        group.MapGet("/refresh/{listId:long}", (long listId, int? pageSize, HttpContext c, ResearchService s, CancellationToken ct) =>
            Handle(c, () => s.RefreshTasksAsync(Account(c), listId, pageSize ?? 20, ct)));
        group.MapPost("", (ResearchSubmission r, HttpContext c, ResearchService s, CancellationToken ct) => Handle(c, () => s.SubmitAsync(Account(c), r, ct)))
            .WithMetadata(new RequestSizeLimitAttribute(65536)).RequireRateLimiting(ProductImportEndpoints.SubmissionRatePolicy);
        group.MapPost("/select", (ResearchSelection r, HttpContext c, ResearchService s, CancellationToken ct) => Handle(c, () => s.SelectAsync(Account(c), r, ct)))
            .RequireRateLimiting(ProductImportEndpoints.SubmissionRatePolicy);
        group.MapPut("/issues/{id:guid}", (Guid id, ResearchIssueUpdate r, HttpContext c, ResearchService s, CancellationToken ct) => Handle(c, () => s.ResolveAsync(Account(c), id, r, ct)))
            .RequireRateLimiting(ProductImportEndpoints.SubmissionRatePolicy);
        group.MapPost("/generic/{listId:long}", (long listId, GenericResearchItem r, HttpContext c, ProductLibraryService s, CancellationToken ct) =>
            Handle(c, () => s.AddGenericAsync(Account(c), listId, r.Name, r.Quantity, ct))).RequireRateLimiting(ProductImportEndpoints.SubmissionRatePolicy);
    }
    private static long Account(HttpContext c) => long.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);
    private static async Task<IResult> Handle<T>(HttpContext c, Func<Task<T>> action)
    {
        c.Response.Headers.CacheControl = "no-store";
        // Optional UI consistency fence, never an identity/authorization source.
        if (c.Request.Headers.TryGetValue("X-MyShoppingList-Account", out var expected)
            && (expected.Count != 1 || !long.TryParse(expected.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id != Account(c)))
            return Results.Problem(statusCode: 409, title: "Your account changed. Reload research before continuing.");
        try { return Results.Ok(await action()); }
        catch (UserImportException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
        catch (ModelContextProtocol.McpException e) { return Results.Problem(statusCode: e.Message == "request_id_conflict" ? 409 : 403,
            title: e.Message == "request_id_conflict" ? "This request ID was already used with different information." : "This account is unavailable for research."); }
    }
}
public sealed record GenericResearchItem(string Name, int Quantity = 1);
