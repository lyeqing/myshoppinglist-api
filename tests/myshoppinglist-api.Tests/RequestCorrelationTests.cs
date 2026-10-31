using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using myshoppinglist_api.Security;
using myshoppinglist_api.Workers;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class RequestCorrelationTests
{
    [Fact]
    public async Task Unexpected_failures_have_safe_details_and_a_server_correlation_reference()
    {
        await using var app = new CorrelationFactory();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new("https://localhost"), HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('a', 64));
        using var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("The service could not complete this request.", body.RootElement.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
        var text = body.RootElement.ToString();
        Assert.DoesNotContain("Correlation tests must not access a database", text);
        Assert.DoesNotContain("InvalidOperationException", text);
        Assert.DoesNotContain(new string('a', 64), text);
    }

    [Fact]
    public async Task Error_responses_match_rendered_logs_without_logging_private_request_contents()
    {
        var app = new CorrelationFactory();
        var traces = new List<string>();
        var privateValues = new[]
        {
            "body-password-do-not-log", "bearer-do-not-log", "cookie-do-not-log",
            "query-token-do-not-log", "private-list-note-do-not-log"
        };
        var templates = new List<string>();
        try
        {
            using var client = app.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new("https://localhost"), HandleCookies = false, AllowAutoRedirect = false
            });
            var configuration = app.Services.GetRequiredService<IConfiguration>();
            foreach (var name in new[] { "Console", "File" })
            {
                var sink = Assert.Single(configuration.GetSection("Serilog:WriteTo").GetChildren(),
                    c => c["Name"] == name);
                templates.Add(Assert.IsType<string>(sink["Args:outputTemplate"]));
            }
            templates.Add(Program.BootstrapConsoleOutputTemplate);
            templates.Add(Program.BootstrapFileOutputTemplate);

            for (var index = 0; index < 2; index++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    "/api/auth/login?token=" + privateValues[3]);
                request.Headers.Add(CookieRequestProtection.HeaderName, "1");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", privateValues[1]);
                request.Headers.Add("Cookie", "unrelated_cookie=" + privateValues[2]);
                request.Headers.Add("X-Private-Note", privateValues[4]);
                // Deliberately malformed after the private value: model binding must fail before account access.
                request.Content = new StringContent("{\"password\":\"" + privateValues[0] + "\",", Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                var raw = await response.Content.ReadAsStringAsync();
                using var problem = JsonDocument.Parse(raw);
                Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
                var trace = problem.RootElement.GetProperty("traceId").GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(trace));
                traces.Add(trace);
                foreach (var value in privateValues) Assert.DoesNotContain(value, raw);
            }
            Assert.Equal(0, app.DatabaseGuard.Attempts);
        }
        finally { await app.DisposeAsync(); }

        Assert.NotEqual(traces[0], traces[1]);
        var requests = app.Events.Entries.Where(e => e.Properties.ContainsKey("Elapsed")
            && e.Properties.TryGetValue("RequestPath", out var path)
            && path is ScalarValue { Value: "/api/auth/login" }).ToArray();
        Assert.Equal(2, requests.Length);
        foreach (var trace in traces)
        {
            var traceComponent = trace.StartsWith("00-", StringComparison.Ordinal) ? trace.Split('-')[1] : trace;
            var entry = Assert.Single(requests, e => e.TraceId?.ToString() == traceComponent);
            var requestId = Assert.IsType<string>(Assert.IsType<ScalarValue>(entry.Properties["RequestId"]).Value);
            Assert.False(string.IsNullOrWhiteSpace(requestId));
            foreach (var template in templates)
            {
                var rendered = Render(entry, template);
                Assert.Contains("[TraceId:" + traceComponent + "]", rendered);
                Assert.Contains(requestId, rendered);
                Assert.Contains("HTTP POST /api/auth/login", rendered);
                foreach (var value in privateValues) Assert.DoesNotContain(value, rendered);
            }
            Assert.Contains(traceComponent, app.FileOutput);
            Assert.Contains(requestId, app.FileOutput);
        }
        foreach (var value in privateValues)
        {
            Assert.DoesNotContain(value, app.FileOutput);
            foreach (var template in templates)
                Assert.DoesNotContain(value, string.Concat(app.Events.Entries.Select(e => Render(e, template))));
        }
    }

    private static string Render(LogEvent entry, string template)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        new MessageTemplateTextFormatter(template, CultureInfo.InvariantCulture).Format(entry, writer);
        return writer.ToString();
    }

    private sealed class Events : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Entries { get; } = new();
        public void Emit(LogEvent logEvent) => Entries.Enqueue(logEvent);
    }

    private sealed class NoDatabaseAccess : DbConnectionInterceptor
    {
        public int Attempts { get; private set; }
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result)
        {
            Attempts++;
            throw new InvalidOperationException("Correlation tests must not access a database.");
        }
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("Correlation tests must not access a database.");
        }
    }

    private sealed class CorrelationFactory : WebApplicationFactory<Program>
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "myshoppinglist-correlation-" + Guid.NewGuid().ToString("N"));
        public Events Events { get; } = new();
        public NoDatabaseAccess DatabaseGuard { get; } = new();
        public string FileOutput { get; private set; } = "";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(directory);
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Serilog:WriteTo:1:Args:path"] = Path.Combine(directory, "requests-.log") }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ILogEventSink>(Events);
                services.PostConfigure<UserExtensionOptions>(o => o.PersonalCapturesEnabled = true);
                services.PostConfigure<ProductImportOptions>(o => o.Enabled = false);
                services.PostConfigure<PriceRefreshOptions>(o => o.Enabled = false);
                foreach (var registration in services.Where(s => s.ServiceType == typeof(IHostedService)
                    && s.ImplementationType?.Namespace == typeof(ProductImportWorker).Namespace).ToArray())
                    services.Remove(registration);
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>();
                services.AddDbContext<MyShoppingListDbContext>(o => o
                    .UseNpgsql("Host=127.0.0.1;Port=1;Database=correlation_never_connect;Timeout=1")
                    .AddInterceptors(DatabaseGuard));
            });
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await base.DisposeAsync();
                await Serilog.Log.CloseAndFlushAsync();
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    var files = Directory.GetFiles(directory, "requests-*.log");
                    FileOutput = string.Concat(files.Select(File.ReadAllText));
                    foreach (var file in files) File.Delete(file);
                    Directory.Delete(directory); // Non-recursive: unexpected contents must never be erased.
                }
            }
        }
    }
}
