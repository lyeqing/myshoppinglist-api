using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace myshoppinglist_api.Tests;

public class RequestCancellationTests
{
    [Fact]
    public async Task Client_cancellation_is_499_without_a_response_body_or_error_log()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var context = new DefaultHttpContext { RequestAborted = cancelled.Token };
        context.Response.Body = new MemoryStream();
        var sink = new Events();
        await RunAsync(context, _ => throw new OperationCanceledException(cancelled.Token), sink);
        Assert.Equal(499, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        Assert.DoesNotContain(sink.Entries, entry => entry.Level >= LogEventLevel.Error);
        Assert.Single(sink.Entries);
    }

    [Fact]
    public async Task Client_cancellation_does_not_rewrite_an_already_started_response()
    {
        var context = new DefaultHttpContext { RequestAborted = new CancellationToken(true) };
        context.Features.Set<IHttpResponseFeature>(new StartedResponse());
        var sink = new Events();
        await RunAsync(context, _ => throw new TaskCanceledException(), sink);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.DoesNotContain(sink.Entries, entry => entry.Level >= LogEventLevel.Error);
    }

    [Fact]
    public async Task Cancellation_without_a_client_disconnect_remains_an_error()
    {
        var context = new DefaultHttpContext();
        var sink = new Events();
        var failure = new OperationCanceledException("An internal operation was cancelled");
        Assert.Same(failure, await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RunAsync(context, _ => throw failure, sink)));
        Assert.Contains(sink.Entries, entry => entry.Level == LogEventLevel.Error && entry.Exception == failure);
    }

    [Fact]
    public async Task Genuine_failure_is_not_hidden_even_when_the_client_disconnected()
    {
        var context = new DefaultHttpContext { RequestAborted = new CancellationToken(true) };
        var sink = new Events();
        var failure = new InvalidOperationException("Server failure");
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(context, _ => throw failure, sink)));
        Assert.Contains(sink.Entries, entry => entry.Level == LogEventLevel.Error && entry.Exception == failure);
    }

    [Fact]
    public async Task Server_error_response_without_an_exception_is_still_logged()
    {
        var context = new DefaultHttpContext();
        var sink = new Events();
        await RunAsync(context, c => { c.Response.StatusCode = 503; return Task.CompletedTask; }, sink);
        Assert.Contains(sink.Entries, entry => entry.Level == LogEventLevel.Error);
    }

    private static async Task RunAsync(HttpContext context, RequestDelegate endpoint, Events sink)
    {
        using var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        using var services = new ServiceCollection().AddSerilog(logger).BuildServiceProvider();
        context.RequestServices = services;
        var app = new ApplicationBuilder(services);
        app.UseSerilogRequestLogging(options => { options.Logger = logger; options.GetLevel = Program.RequestLogLevel; });
        app.Use(Program.HandleRequestCancellationAsync);
        app.Run(endpoint);
        await app.Build()(context);
    }

    private sealed class Events : ILogEventSink
    {
        public List<LogEvent> Entries { get; } = [];
        public void Emit(LogEvent logEvent) => Entries.Add(logEvent);
    }

    private sealed class StartedResponse : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}
