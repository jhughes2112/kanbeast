using CommandLine;
using KanBeast.Server.Services;
using KanBeast.Server.Hubs;

var builder = WebApplication.CreateBuilder(args);

var serverOptions = Parser.Default.ParseArguments<ServerOptions>(args)
    .MapResult(options => options, _ => new ServerOptions());

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddOpenApi();

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5000", "http://localhost:8080")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Register application services
builder.Services.AddSingleton<ITicketService, TicketService>();
builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<IWorkerOrchestrator, WorkerOrchestrator>();
builder.Services.AddSingleton(serverOptions);

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.UseStaticFiles();
app.UseRouting();
app.MapControllers();
app.MapHub<KanbanHub>("/hubs/kanban");

// Serve the frontend
app.MapFallbackToFile("index.html");

app.Run();
