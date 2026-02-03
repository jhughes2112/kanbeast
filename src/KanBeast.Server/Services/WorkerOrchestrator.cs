using System.Diagnostics;
using System.Text;
using KanBeast.Server.Models;

namespace KanBeast.Server.Services;

// Controls worker container lifecycle operations.
public interface IWorkerOrchestrator
{
    Task<string> StartWorkerAsync(string ticketId);
    Task<bool> StopWorkerAsync(string workerId);
    Task<Dictionary<string, string>> GetActiveWorkersAsync();
}

// Launches worker containers and tracks active worker processes.
public class WorkerOrchestrator : IWorkerOrchestrator
{
    private readonly Dictionary<string, string> _activeWorkers = new Dictionary<string, string>();
    private readonly ITicketService _ticketService;
    private readonly ILogger<WorkerOrchestrator> _logger;
    private readonly ServerOptions _options;
    private const string WorkerEnvDirectory = "/app/env";

    public WorkerOrchestrator(
        ITicketService ticketService,
        ILogger<WorkerOrchestrator> logger,
        ServerOptions options)
    {
        _ticketService = ticketService;
        _logger = logger;
        _options = options;
    }

    public async Task<string> StartWorkerAsync(string ticketId)
    {
        Ticket? ticket = await _ticketService.GetTicketAsync(ticketId);
        if (ticket == null)
        {
            throw new InvalidOperationException($"Ticket {ticketId} not found");
        }

        string workerId = Guid.NewGuid().ToString();

        await EnsureDockerNetworkAsync();

        string containerName = $"kanbeast-worker-{workerId}";
        Dictionary<string, string> envVars = BuildWorkerEnvironment(ticketId);

        _logger.LogInformation("Starting worker {WorkerId} for ticket {TicketId}", workerId, ticketId);

        StringBuilder envArgsBuilder = new StringBuilder();
        foreach ((string Key, string Value) in envVars)
        {
            if (envArgsBuilder.Length > 0)
            {
                envArgsBuilder.Append(' ');
            }

            envArgsBuilder.Append($"-e \"{EscapeDockerArg(Key)}={EscapeDockerArg(Value)}\"");
        }

        string envArgs = envArgsBuilder.ToString();
        string args = $"run -d --name {EscapeDockerArg(containerName)} --network {EscapeDockerArg(_options.DockerNetwork)} {envArgs} {EscapeDockerArg(_options.WorkerImage)} dotnet /app/worker/KanBeast.Worker.dll";
        string containerId = await RunDockerCommandAsync(args);

        // Update ticket with worker ID
        ticket.WorkerId = workerId;
        await _ticketService.AddActivityLogAsync(ticketId, $"Worker {workerId} assigned (container {containerName})");

        // Store the worker ID and status
        _activeWorkers[workerId] = containerId;

        return workerId;
    }

    public Task<bool> StopWorkerAsync(string workerId)
    {
        if (!_activeWorkers.ContainsKey(workerId))
        {
            return Task.FromResult(false);
        }

        _logger.LogInformation($"Stopping worker {workerId}");
        
        // In production, stop the Docker container
        _activeWorkers.Remove(workerId);
        return Task.FromResult(true);
    }

    public Task<Dictionary<string, string>> GetActiveWorkersAsync()
    {
        Dictionary<string, string> activeWorkers = new Dictionary<string, string>(_activeWorkers);

        return Task.FromResult(activeWorkers);
    }

    private Dictionary<string, string> BuildWorkerEnvironment(string ticketId)
    {
        return new Dictionary<string, string>
        {
            ["TICKET_ID"] = ticketId,
            ["SERVER_URL"] = _options.ServerUrl
        };
    }

    private async Task EnsureDockerNetworkAsync()
    {
        try
        {
            await RunDockerCommandAsync($"network inspect {EscapeDockerArg(_options.DockerNetwork)}");
        }
        catch
        {
            await RunDockerCommandAsync($"network create {EscapeDockerArg(_options.DockerNetwork)}");
        }
    }

    private async Task<string> RunDockerCommandAsync(string arguments)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo("docker", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using Process process = new Process { StartInfo = startInfo };
        process.Start();

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Docker command failed: {error}".Trim());
        }

        return output.Trim();
    }

    private static string EscapeDockerArg(string value)
    {
        string escaped = value
            .Replace("\r", "")
            .Replace("\n", "\\n")
            .Replace("\"", "\\\"");

        return escaped;
    }
}
