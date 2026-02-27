using CommandLine;
using KanBeast.Server;
using KanBeast.Worker;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Entrypoint;

public class Program
{
	public static async Task<int> Main(string[] args)
	{
		Environment.CurrentDirectory = "/workspace";  // for some reason, Visual Studio forces the working directory to /app when in debug mode, so I have to force it back to /workspace.
		using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
		{
			builder.AddConsole();
			builder.SetMinimumLevel(LogLevel.Information);
		});

		ILogger<Program> logger = loggerFactory.CreateLogger<Program>();
		logger.LogInformation("Entrypoint starting...");

		using CancellationTokenSource cts = new CancellationTokenSource();

		Console.CancelKeyPress += (sender, e) =>
		{
			e.Cancel = true;
			logger.LogWarning("Shutdown requested via Ctrl+C");
			cts.Cancel();
		};

		if (args == null || args.Length == 0)
		{
			logger.LogInformation("No arguments provided — starting server.");
			await KanBeast.Server.Server.Run();
			return 0;
		}

		int exit = await Parser.Default.ParseArguments<WorkerOptions>(args).MapResult(async (WorkerOptions opts) =>
		{
			return await Worker.RunAsync(opts.TicketId, opts.ServerUrl, opts.RepoPath, logger, cts.Token);
		}, errs =>
		{
			logger.LogError("Invalid arguments. This executable is the entrypoint for both the Server (no args) and the Worker (--ticket-id --server-url).");
			Console.WriteLine("Usage:\n  [no args]              Start the server\n  --ticket-id <id> --server-url <url> [--repo <path>]    Start the worker");
			return Task.FromResult(1);
		});

		return exit;
	}
}

public class WorkerOptions
{
	[Option("ticket-id", Required = true, HelpText = "Ticket id for the worker.")]
	public required string TicketId { get; set; }

	[Option("server-url", Required = true, HelpText = "Server URL for the worker.")]
	public required string ServerUrl { get; set; }

	[Option("repo", Required = false, Default = "/repo", HelpText = "Path where the git repository will be cloned.")]
	public string RepoPath { get; set; } = string.Empty;
}
