namespace myshoppinglist_api.Security;

public static class SessionCookie
{
    public const string Name = "myshoppinglist_session";
    private static CookieOptions Options(bool secure) => new()
    { HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Lax, Path = "/", IsEssential = true };
    public static void Write(HttpContext context, string token, DateTime expires, bool secure)
    {
        var options = Options(secure);
        options.Expires = new DateTimeOffset(expires, TimeSpan.Zero);
        context.Response.Cookies.Append(Name, token, options);
    }
    public static void Delete(HttpContext context, bool secure) => context.Response.Cookies.Delete(Name, Options(secure));
}
