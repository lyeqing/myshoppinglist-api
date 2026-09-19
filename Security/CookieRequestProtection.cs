namespace myshoppinglist_api.Security;

// Custom-header protection relies on the same-origin policy: no credentialed CORS is enabled.
// Require it even for anonymous trial creation, so cross-site forms cannot replace a session.
public sealed class CookieRequestProtection(RequestDelegate next)
{
    public const string HeaderName = "X-MyShoppingList-Request";
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!request.Path.StartsWithSegments("/api") || HttpMethods.IsGet(request.Method)
            || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        { await next(context); return; }
        var bearer = context.User.Identity?.IsAuthenticated == true
            && context.User.FindFirst(SessionTokenAuthenticationHandler.TransportClaim)?.Value == "bearer";
        if (!bearer)
        {
            var origin = request.Headers.Origin;
            var sameOrigin = origin.Count == 0 || origin.Count == 1
                && Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri)
                && uri.GetLeftPart(UriPartial.Authority).Equals($"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0;
            if (request.Headers[HeaderName].Count != 1 || request.Headers[HeaderName] != "1"
                || request.Headers["Sec-Fetch-Site"] == "cross-site" || !sameOrigin)
            {
                await Results.Problem(statusCode: 403, title: "The request failed browser request protection.").ExecuteAsync(context);
                return;
            }
        }
        await next(context);
    }
}
