using System.Diagnostics.Metrics;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog;
using ModelContextProtocol;
using System.Text.Json;
using System.Diagnostics;
using System.Security.Claims;
using OpenIddict.Abstractions;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Mcp;

public static class McpToolAudit
{
    public const string Source = "myshoppinglist_api.Mcp.McpToolAudit";
    private static readonly Meter Meter = new("MyShoppingList.Mcp", "1.0");
    private static readonly Counter<long> Calls = Meter.CreateCounter<long>("myshoppinglist.mcp.tool.calls");
    private static readonly HashSet<string> Names = ["get_my_lists", "get_current_list", "get_list_items", "search_my_products",
        "create_list", "set_current_list", "add_product_to_list", "update_list_item", "remove_list_item",
        "get_extension_status", "request_extension_capture", "request_extension_search", "get_extension_task_status",
        "get_research_instructions", "get_shopping_context", "get_research_review", "get_price_refresh_tasks",
        "submit_product_research", "resolve_research_issue"];

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, ct) =>
        {
            var name = request.Params?.Name is { } candidate && Names.Contains(candidate) ? candidate : "unknown";
            var outcome = "failed";
            var started = Stopwatch.GetTimestamp();
            try
            {
                var result = await next(request, ct);
                outcome = result.IsError == true ? "rejected" : "success";
                return result;
            }
            catch (McpWriteConflictException conflict)
            {
                outcome = "rejected";
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "conflict: refresh before retrying" }],
                    StructuredContent = JsonSerializer.SerializeToElement(conflict.Details, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            }
            catch (McpException error)
            {
                outcome = "rejected";
                var code = error.Message is "authentication_required" or "invalid_identity" or "insufficient_scope"
                    or "account_unavailable" or "account_not_supported" or "not_found" or "invalid_arguments" or "conflict"
                    or "request_id_conflict" or "confirmation_required" or "list_limit_reached" or "extension_unavailable" ? error.Message : "request_rejected";
                return Error(code);
            }
            catch (ArgumentException) { outcome = "rejected"; return Error("invalid_arguments"); }
            catch (System.Text.Json.JsonException) { outcome = "rejected"; return Error("invalid_arguments"); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { outcome = "cancelled"; throw; }
            finally
            {
                Calls.Add(1, new KeyValuePair<string, object?>("tool", name), new KeyValuePair<string, object?>("outcome", outcome));
                // Never log arguments, result content, user IDs, tokens, client URLs or exception messages.
                Log.ForContext("SourceContext", Source).Information("MCP tool {Tool} completed with {Outcome}", name, outcome);
                if (request.Services?.GetService<OperationalEvents>() is { } events)
                {
                    var context = request.Services.GetService<IHttpContextAccessor>()?.HttpContext;
                    var account = long.TryParse(context?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (long?)null;
                    await events.RecordAsync("mcp", name, outcome, account, Activity.Current?.TraceId.ToString(),
                        (int)Math.Min(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 120000), client: context?.User.GetPresenters().FirstOrDefault());
                }
            }
        };

    private static CallToolResult Error(string code) => new()
    {
        IsError = true, Content = [new TextContentBlock { Text = code }],
        StructuredContent = JsonSerializer.SerializeToElement(new { version = 1, error = code })
    };
}
