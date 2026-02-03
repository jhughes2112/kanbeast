using CommandLine;
using KanBeast.Server.Services;
using KanBeast.Server.Hubs;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

ServerOptions serverOptions = Parser.Default.ParseArguments<ServerOptions>(args)
    .MapResult(options => options, _ => new ServerOptions());

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddOpenApi();

// Register application services
builder.Services.AddSingleton<ITicketService, TicketService>();
builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<IWorkerOrchestrator, WorkerOrchestrator>();
builder.Services.AddSingleton(serverOptions);

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
