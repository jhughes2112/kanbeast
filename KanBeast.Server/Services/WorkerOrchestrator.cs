using Docker.DotNet;
using Docker.DotNet.Models;
using KanBeast.Server.Models;

namespace KanBeast.Server.Services;

// Controls worker container lifecycle operations.
public interface IWorkerOrchestrator
{
    Task<string> StartWorkerAsync(string ticketId);
    Task<bool> StopWorkerAsync(string ticketId);
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
        _logger.LogInformation("StartWorkerAsync called for ticket #{TicketId}", ticketId);

        Ticket? ticket = await _ticketService.GetTicketAsync(ticketId);
        if (ticket == null)
        {
            _logger.LogError("Ticket #{TicketId} not found", ticketId);
            throw new InvalidOperationException($"Ticket {ticketId} not found");
        }

        if (!_containerContext.IsRunningInDocker)
        {
            _logger.LogError("Cannot start workers - not running in Docker");
            throw new InvalidOperationException("Cannot start workers when not running in Docker");
        }

        if (_containerContext.Image == null)
        {
            _logger.LogError("Cannot start workers - container image not determined");
            throw new InvalidOperationException("Container image could not be determined");
        }

        string containerName = $"kanbeast-worker-{ticketId}";

        _logger.LogInformation("Creating worker container: {ContainerName} from image {Image}", containerName, _containerContext.Image);

        await EnsureDockerNetworkAsync();

        CreateContainerParameters createParams = new CreateContainerParameters
        {
            Image = _containerContext.Image,
            Name = containerName,
            Entrypoint = new List<string>
            {
                "dotnet",
                "/app/worker/KanBeast.Worker.dll",
                "--ticket-id", ticketId,
                "--server-url", _containerContext.ServerUrl,
                "--repo", "/repo"
            },
            HostConfig = new HostConfig
            {
                NetworkMode = _containerContext.Network,
                Mounts = _containerContext.Mounts,
                AutoRemove = true
            }
        };

        _logger.LogInformation("Entrypoint: dotnet /app/worker/KanBeast.Worker.dll --ticket-id {TicketId} --server-url {ServerUrl}", ticketId, _containerContext.ServerUrl);

        _logger.LogInformation("Creating container...");
        CreateContainerResponse response = await _dockerClient.Containers.CreateContainerAsync(createParams);
        string containerId = response.ID;
        _logger.LogInformation("Container created: {ContainerId}", containerId);

        _logger.LogInformation("Starting container...");
        await _dockerClient.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
        _logger.LogInformation("Container started successfully");

        // Update ticket with container name
        ticket.ContainerName = containerName;
        await _ticketService.AddActivityLogAsync(ticketId, $"Worker assigned (container {containerName})");

        // Store the ticket ID and container ID mapping
        _activeWorkers[ticketId] = containerId;

        return containerName;
    }

    public async Task<bool> StopWorkerAsync(string ticketId)
    {
        if (!_activeWorkers.TryGetValue(ticketId, out string? containerId))
        {
            return false;
        }

        _logger.LogInformation("Stopping worker for ticket {TicketId}", ticketId);

        try
        {
            await _dockerClient.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 10 });
        }
        catch (DockerContainerNotFoundException)
        {
            _logger.LogInformation("Worker container for ticket {TicketId} already removed", ticketId);
        }

        _activeWorkers.Remove(ticketId);
        return true;
    }

    public Task<Dictionary<string, string>> GetActiveWorkersAsync()
    {
        Dictionary<string, string> activeWorkers = new Dictionary<string, string>(_activeWorkers);

        return Task.FromResult(activeWorkers);
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
