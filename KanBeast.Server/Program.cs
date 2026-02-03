using System.Text.Json.Serialization;
using KanBeast.Server;
using KanBeast.Server.Services;
using KanBeast.Server.Hubs;
using Microsoft.Extensions.Logging.Console;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Configure clean console logging - just timestamp and message
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.FormatterName = MinimalConsoleFormatter.FormatterName;
});
builder.Logging.AddConsoleFormatter<MinimalConsoleFormatter, ConsoleFormatterOptions>();

// Add services to the container
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddSignalR();
builder.Services.AddOpenApi();

// Initialize container context (detects Docker environment)
ILoggerFactory loggerFactory = LoggerFactory.Create(b =>
{
    b.AddConsole(options => options.FormatterName = MinimalConsoleFormatter.FormatterName);
    b.AddConsoleFormatter<MinimalConsoleFormatter, ConsoleFormatterOptions>();
});
ILogger<ContainerContext> contextLogger = loggerFactory.CreateLogger<ContainerContext>();
ContainerContext containerContext = await ContainerContext.CreateAsync(contextLogger);

// Register application services
builder.Services.AddSingleton<ITicketService, TicketService>();
builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<IWorkerOrchestrator, WorkerOrchestrator>();
builder.Services.AddSingleton(containerContext);

WebApplication app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseStaticFiles();
app.UseRouting();
app.MapControllers();
app.MapHub<KanbanHub>("/hubs/kanban");

// Serve the frontend for any unmatched routes
app.MapFallbackToFile("index.html");

app.Run();
