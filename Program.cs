using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Data;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Security;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Services;
using myshoppinglist_api.Workers;
using System.Net;
using Serilog;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using myshoppinglist_api.Endpoints;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();
try
{
    var builder = WebApplication.CreateBuilder(args);
    // Local credentials are excluded from source control. Environment variables take precedence.
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables();
    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration).ReadFrom.Services(services).Enrich.FromLogContext());
    builder.Services.AddDbContext<MyShoppingListDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("MyShoppingList")));
    builder.Services.AddSingleton(new RetailerCatalog());
    builder.Services.AddSingleton<ProductUrlValidator>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
    builder.Services.AddScoped<TrialSessionService>();
    builder.Services.AddAuthentication(SessionTokenAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, SessionTokenAuthenticationHandler>(SessionTokenAuthenticationHandler.SchemeName, _ => { });
    builder.Services.AddAuthorization();
    builder.Services.AddRateLimiter(_ => { });
    builder.Services.AddOptions<RateLimiterOptions>().Configure<IOptions<AuthOptions>>((limits, auth) =>
    {
        limits.AddPolicy(AuthEndpoints.TrialRatePolicy, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = auth.Value.TrialRequestsPerWindow, Window = TimeSpan.FromSeconds(auth.Value.TrialWindowSeconds),
                QueueLimit = 0, AutoReplenishment = true
            }));
        limits.OnRejected = async (context, token) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retry.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await Results.Problem(statusCode: 429, title: "Too many trial requests. Please try again later.").ExecuteAsync(context.HttpContext);
        };
    });
    builder.Services.AddSingleton<ProductNormalisationService>();
    builder.Services.AddSingleton<ProductMatchingService>();
    builder.Services.AddScoped<ProductService>();
    builder.Services.AddScoped<ShopProductService>();
    builder.Services.AddScoped<PriceService>();
    builder.Services.AddScoped<SourceProductPersistenceService>();
    builder.Services.AddScoped<ProductImportJobService>();
    builder.Services.AddScoped<ProductImportProcessor>();
    builder.Services.AddOptions<ProductImportOptions>().BindConfiguration(ProductImportOptions.SectionName)
        .ValidateDataAnnotations()
        .Validate(o => o.RenewalSeconds * 2 < o.LeaseSeconds, "Lease duration must exceed twice the renewal interval.")
        .ValidateOnStart();
    builder.Services.AddHostedService<ProductImportWorker>();
    builder.Services.AddOptions<PriceOptions>().BindConfiguration(PriceOptions.SectionName)
        .ValidateDataAnnotations().ValidateOnStart();
    builder.Services.AddSingleton(services => new SafeRetailerConnection(services.GetRequiredService<RetailerCatalog>()));
    builder.Services.AddOptions<RetailerHttpOptions>().BindConfiguration(RetailerHttpOptions.SectionName)
        .ValidateDataAnnotations().ValidateOnStart();
    builder.Services.AddHttpClient(RetailerHttpClient.ClientName, client =>
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MyShoppingList/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
    }).ConfigurePrimaryHttpMessageHandler(services => new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        MaxConnectionsPerServer = 4, PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxResponseHeadersLength = 32,
        ConnectCallback = (context, token) => services.GetRequiredService<SafeRetailerConnection>().ConnectAsync(context.DnsEndPoint, token)
    });
    builder.Services.AddScoped<RetailerHttpClient>();
    builder.Services.AddSingleton<ColesProductParser>();
    builder.Services.AddScoped<IShopProductProvider, ColesProductProvider>();
    builder.Services.AddScoped(services => new RetailerProviderRegistry(services.GetRequiredService<RetailerCatalog>(),
        services.GetServices<IShopProductProvider>()));
    builder.Services.AddProblemDetails();
    builder.Services.AddOpenApi();
    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    var app = builder.Build();
    app.UseExceptionHandler();
    app.UseSerilogRequestLogging();
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/openapi/v1.json", "MyShoppingList API v1");
            options.DocumentTitle = "MyShoppingList API";
        });
    }
    else app.UseHttpsRedirection();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseMiddleware<CookieRequestProtection>();
    app.UseRateLimiter();
    app.MapAuthEndpoints();
    app.MapGet("/", () => "MyShoppingList API").ExcludeFromDescription();
    app.Run();
}
catch (Exception exception) when (exception is not HostAbortedException)
{
    Log.Fatal(exception, "MyShoppingList API terminated unexpectedly.");
    throw;
}
finally { Log.CloseAndFlush(); }

public partial class Program { }
