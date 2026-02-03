using Docker.DotNet;
using Docker.DotNet.Models;
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
    private readonly ContainerContext _containerContext;
    private readonly DockerClient _dockerClient;

    public WorkerOrchestrator(
        ITicketService ticketService,
        ILogger<WorkerOrchestrator> logger,
        ContainerContext containerContext)
    {
        _ticketService = ticketService;
        _logger = logger;
        _containerContext = containerContext;
        _dockerClient = new DockerClientConfiguration().CreateClient();
    }

    public async Task<string> StartWorkerAsync(string ticketId)
    {
        Ticket? ticket = await _ticketService.GetTicketAsync(ticketId);
        if (ticket == null)
        {
            throw new InvalidOperationException($"Ticket {ticketId} not found");
        }

        if (!_containerContext.IsRunningInDocker)
        {
            throw new InvalidOperationException("Cannot start workers when not running in Docker");
        }

        if (_containerContext.Image == null)
        {
            throw new InvalidOperationException("Container image could not be determined");
        }

        string workerId = Guid.NewGuid().ToString();

        await EnsureDockerNetworkAsync();

        string containerName = $"kanbeast-worker-{workerId}";
        Dictionary<string, string> envVars = BuildWorkerEnvironment(ticketId);

        _logger.LogInformation("Starting worker {WorkerId} for ticket {TicketId}", workerId, ticketId);

        List<string> envList = new List<string>();
        foreach ((string key, string value) in envVars)
        {
            envList.Add($"{key}={value}");
        }

        CreateContainerParameters createParams = new CreateContainerParameters
        {
            Image = _containerContext.Image,
            Name = containerName,
            Env = envList,
            Cmd = new List<string> { "dotnet", "/app/worker/KanBeast.Worker.dll" },
            HostConfig = new HostConfig
            {
                NetworkMode = _containerContext.Network,
                Mounts = _containerContext.Mounts
            }
        };

        CreateContainerResponse response = await _dockerClient.Containers.CreateContainerAsync(createParams);
        string containerId = response.ID;

        await _dockerClient.Containers.StartContainerAsync(containerId, new ContainerStartParameters());

        // Update ticket with worker ID
        ticket.WorkerId = workerId;
        await _ticketService.AddActivityLogAsync(ticketId, $"Worker {workerId} assigned (container {containerName})");

        // Store the worker ID and container ID
        _activeWorkers[workerId] = containerId;

        return workerId;
    }

    public async Task<bool> StopWorkerAsync(string workerId)
    {
        if (!_activeWorkers.TryGetValue(workerId, out string? containerId))
        {
            return false;
        }

        _logger.LogInformation("Stopping worker {WorkerId}", workerId);

        await _dockerClient.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 10 });
        await _dockerClient.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });

        _activeWorkers.Remove(workerId);
        return true;
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
            ["SERVER_URL"] = _containerContext.ServerUrl
        };
    }

    private async Task EnsureDockerNetworkAsync()
    {
        if (_containerContext.Network == null)
        {
            return;
        }

        IList<NetworkResponse> networks = await _dockerClient.Networks.ListNetworksAsync(new NetworksListParameters());
        bool networkExists = false;
        foreach (NetworkResponse network in networks)
        {
            if (network.Name == _containerContext.Network)
            {
                networkExists = true;
                break;
            }
        }

        if (!networkExists)
        {
            await _dockerClient.Networks.CreateNetworkAsync(new NetworksCreateParameters { Name = _containerContext.Network });
        }
    }
}
