using System.Text.Json.Serialization;
using KanBeast.Server;
using KanBeast.Server.Services;
using KanBeast.Server.Hubs;
using Microsoft.Extensions.Logging.Console;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;

namespace KanBeast.Server;

public class Server
{
    public static async Task Run()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
		Environment.CurrentDirectory = "/workspace";  // for some reason, Visual Studio forces the working directory to /app when in debug mode, so I have to force it back to /workspace.

        // Configure clean console logging - just timestamp and message
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.FormatterName = MinimalConsoleFormatter.FormatterName;
        });
        builder.Logging.AddConsoleFormatter<MinimalConsoleFormatter, ConsoleFormatterOptions>();

        // Reduce ASP.NET Core routing/action noise
        builder.Logging.AddFilter("Microsoft.AspNetCore.Routing", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Mvc", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting", LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.AspNetCore.StaticFiles", LogLevel.Warning);

        // Add services to the container
        builder.Services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });
        builder.Services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
        })
        .AddJsonProtocol(options =>
        {
            options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
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
        builder.Services.AddSingleton<ConversationStore>();
        builder.Services.AddSingleton<WorkerOrchestrator>();
        builder.Services.AddSingleton<IWorkerOrchestrator>(sp => sp.GetRequiredService<WorkerOrchestrator>());
        builder.Services.AddHostedService<WorkerOrchestrator>(sp => sp.GetRequiredService<WorkerOrchestrator>());
        builder.Services.AddHostedService<ActiveTicketWatchdog>();
        builder.Services.AddSingleton(containerContext);

        // Give workers enough time to commit, push, and move tickets to Backlog during shutdown.
        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromMinutes(2);
        });

        WebApplication app = builder.Build();

        // Configure the HTTP request pipeline
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                ctx.Context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
                ctx.Context.Response.Headers.Append("Pragma", "no-cache");
                ctx.Context.Response.Headers.Append("Expires", "0");
            }
        });
        app.UseRouting();
        app.MapControllers();
        app.MapHub<KanbanHub>("/hubs/kanban");

        // Serve the frontend for any unmatched routes
        app.MapFallbackToFile("index.html");

        app.Run();
    }
}
