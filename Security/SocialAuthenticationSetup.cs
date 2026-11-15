using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Security;

public static class SocialAuthenticationSetup
{
    public const string TicketScheme = "SocialTicket";
    public const string TicketCookie = "mssl.external";
    public static string Scheme(string provider) => "social." + provider;
    public static void AddSocialAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<SocialAuthenticationOptions>(configuration.GetSection("SocialAuthentication"));
        services.AddScoped<SocialAuthenticationService>();
        var settings = configuration.GetSection("SocialAuthentication").Get<SocialAuthenticationOptions>() ?? new();
        Validate(settings, environment.IsDevelopment());
        if (!settings.Enabled) return;
        if (!environment.IsDevelopment() && (string.IsNullOrWhiteSpace(configuration["AccountSecurity:KeyDirectory"])
            || string.IsNullOrWhiteSpace(configuration["AccountSecurity:KeyCertificatePath"])))
            throw new InvalidOperationException("Production social authentication requires persistent protected Data Protection keys.");
        var auth = services.AddAuthentication().AddCookie(TicketScheme, o =>
        {
            o.Cookie.Name = TicketCookie; o.Cookie.HttpOnly = true; o.Cookie.IsEssential = true;
            o.Cookie.SameSite = SameSiteMode.Lax; o.Cookie.Path = "/";
            o.Cookie.SecurePolicy = settings.WebsiteOrigin.StartsWith("https://", StringComparison.Ordinal) ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(10); o.SlidingExpiration = false;
        });
        foreach (var name in SocialAuthenticationOptions.Names.Where(settings.Available))
        {
            var provider = settings.Provider(name)!;
            void Configure(OAuthOptions o)
            {
                o.ClientId = provider.ClientId; o.ClientSecret = provider.ClientSecret;
                o.SignInScheme = TicketScheme; o.CallbackPath = "/api/auth/social/callback/" + name;
                o.SaveTokens = false; o.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(10);
                o.CorrelationCookie.Name = "mssl.correlation." + name + ".";
                o.CorrelationCookie.Path = "/"; o.CorrelationCookie.SameSite = SameSiteMode.Lax;
                o.CorrelationCookie.SecurePolicy = settings.WebsiteOrigin.StartsWith("https://", StringComparison.Ordinal) ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                o.Events = new OAuthEvents
                {
                    OnCreatingTicket = c =>
                    {
                        var subject = c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 512) throw new AuthenticationFailureException("Provider identity unavailable.");
                        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject) };
                        var email = AccountAuthService.NormaliseEmail(c.Principal?.FindFirstValue(ClaimTypes.Email));
                        if (email is not null) claims.Add(new(ClaimTypes.Email, email));
                        var display = c.Principal?.FindFirstValue(ClaimTypes.Name);
                        if (display is { Length: > 0 and <= 200 } && !display.Any(char.IsControl)) claims.Add(new(ClaimTypes.Name, display));
                        c.Principal = new(new ClaimsIdentity(claims, TicketScheme));
                        return Task.CompletedTask;
                    },
                    OnAccessDenied = c => { c.HandleResponse(); c.Response.Redirect(settings.WebsiteOrigin.TrimEnd('/') + "/account/social?error=cancelled"); return Task.CompletedTask; },
                    OnRemoteFailure = c => { c.HandleResponse(); c.Response.Redirect(settings.WebsiteOrigin.TrimEnd('/') + "/account/social?error=failed"); return Task.CompletedTask; }
                };
            }
            if (name == "google") auth.AddGoogle(Scheme(name), o => { Configure(o); o.UsePkce = true; });
            if (name == "facebook") auth.AddFacebook(Scheme(name), o => Configure(o));
            if (name == "microsoft") auth.AddMicrosoftAccount(Scheme(name), o => { Configure(o); o.UsePkce = true; });
        }
    }

    public static void Validate(SocialAuthenticationOptions options, bool development)
    {
        if (!options.Enabled) return;
        if (!Uri.TryCreate(options.WebsiteOrigin, UriKind.Absolute, out var origin) || origin.UserInfo.Length != 0
            || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0
            || !(origin.Scheme == "https" || development && origin.Scheme == "http" && origin.IsLoopback))
            throw new InvalidOperationException("Social authentication requires an explicit HTTPS website origin (loopback HTTP is development-only).");
        foreach (var name in SocialAuthenticationOptions.Names.Where(options.Available))
        {
            var p = options.Provider(name)!;
            if (string.IsNullOrWhiteSpace(p.ClientId) || p.ClientId.Length > 500 || string.IsNullOrWhiteSpace(p.ClientSecret) || p.ClientSecret.Length > 4000)
                throw new InvalidOperationException("Enabled social providers require server-side credentials.");
        }
    }

    // The gateway owns this fixed origin; never accept arbitrary forwarded callback hosts.
    // All social protocol routes (including provider callbacks) are proxied through the website.
    public static void UseSocialAuthenticationOrigin(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<SocialAuthenticationOptions>>().Value;
        if (!settings.Enabled) return;
        var origin = new Uri(settings.WebsiteOrigin);
        app.Use(async (c, next) =>
        {
            if (c.Request.Path.StartsWithSegments("/api/auth/social"))
            { c.Request.Scheme = origin.Scheme; c.Request.Host = new(origin.Authority); }
            await next(c);
        });
    }
}
