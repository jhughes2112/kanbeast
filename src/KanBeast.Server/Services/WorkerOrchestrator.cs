using System.Diagnostics;
using System.Text.Json;
using KanBeast.Server.Models;

namespace KanBeast.Server.Services;

public interface IWorkerOrchestrator
{
    Task<string> StartWorkerAsync(string ticketId);
    Task<bool> StopWorkerAsync(string workerId);
    Task<Dictionary<string, string>> GetActiveWorkersAsync();
}

public class WorkerOrchestrator : IWorkerOrchestrator
{
    private readonly Dictionary<string, string> _activeWorkers = new();
    private readonly ISettingsService _settingsService;
    private readonly ITicketService _ticketService;
    private readonly ILogger<WorkerOrchestrator> _logger;
    private readonly ServerOptions _options;
    private const string WorkerEnvDirectory = "/app/env";

    public WorkerOrchestrator(
        ISettingsService settingsService,
        ITicketService ticketService,
        ILogger<WorkerOrchestrator> logger,
        ServerOptions options)
    {
        _settingsService = settingsService;
        _ticketService = ticketService;
        _logger = logger;
        _options = options;
    }

    public async Task<string> StartWorkerAsync(string ticketId)
    {
        var ticket = await _ticketService.GetTicketAsync(ticketId);
        if (ticket == null)
            throw new InvalidOperationException($"Ticket {ticketId} not found");

        var settings = await _settingsService.GetSettingsAsync();
        var workerId = Guid.NewGuid().ToString();

        await EnsureDockerNetworkAsync();

        var containerName = $"kanbeast-worker-{workerId}";
        var envVars = BuildWorkerEnvironment(ticketId, settings);

        _logger.LogInformation("Starting worker {WorkerId} for ticket {TicketId}", workerId, ticketId);
        var envArgs = string.Join(" ", envVars.Select(envVar => $"-e \"{EscapeDockerArg(envVar.Key)}={EscapeDockerArg(envVar.Value)}\""));

        var args = $"run -d --name {EscapeDockerArg(containerName)} --network {EscapeDockerArg(_options.DockerNetwork)} {envArgs} {EscapeDockerArg(_options.WorkerImage)} dotnet /app/worker/KanBeast.Worker.dll";
        var containerId = await RunDockerCommandAsync(args);

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
            return Task.FromResult(false);

        _logger.LogInformation($"Stopping worker {workerId}");
        
        // In production, stop the Docker container
        _activeWorkers.Remove(workerId);
        return Task.FromResult(true);
    }

    public Task<Dictionary<string, string>> GetActiveWorkersAsync()
    {
        return Task.FromResult(new Dictionary<string, string>(_activeWorkers));
    }

    private Dictionary<string, string> BuildWorkerEnvironment(string ticketId, Settings settings)
    {
        return new Dictionary<string, string>
        {
            ["TICKET_ID"] = ticketId,
            ["SERVER_URL"] = _options.ServerUrl,
            ["GIT_URL"] = settings.GitConfig.RepositoryUrl ?? string.Empty,
            ["GIT_USERNAME"] = settings.GitConfig.Username ?? string.Empty,
            ["GIT_EMAIL"] = settings.GitConfig.Email ?? string.Empty,
            ["GIT_SSH_KEY"] = settings.GitConfig.SshKey ?? string.Empty,
            ["LLM_CONFIGS"] = JsonSerializer.Serialize(settings.LLMConfigs ?? new List<LLMConfig>())
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
        var startInfo = new ProcessStartInfo("docker", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Docker command failed: {error}".Trim());
        }

        return output.Trim();
    }

    private static string EscapeDockerArg(string value)
    {
        return value
            .Replace("\r", "")
            .Replace("\n", "\\n")
            .Replace("\"", "\\\"");
    }
}
