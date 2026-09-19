using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Data;
using Serilog;

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
    app.MapGet("/", () => "MyShoppingList API").ExcludeFromDescription();
    app.Run();
}
catch (Exception exception) when (exception is not HostAbortedException)
{
    Log.Fatal(exception, "MyShoppingList API terminated unexpectedly.");
    throw;
}
finally { Log.CloseAndFlush(); }
