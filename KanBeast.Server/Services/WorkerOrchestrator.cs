using Docker.DotNet;
using Docker.DotNet.Models;
using KanBeast.Server.Models;
using KanBeast.Shared;

namespace KanBeast.Server.Services;

// Controls worker container lifecycle operations.
public interface IWorkerOrchestrator
{
    Task<string> StartWorkerAsync(string ticketId);
    Task<bool> StopWorkerAsync(string ticketId);
    Task<Dictionary<string, string>> GetActiveWorkersAsync();
}

// Launches worker containers and tracks active worker processes.
public class WorkerOrchestrator : IWorkerOrchestrator, IHostedService
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
                AutoRemove = false
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

        try
        {
            await _dockerClient.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });
        }
        catch (DockerContainerNotFoundException)
        {
            // Already gone.
        }

        _activeWorkers.Remove(ticketId);
        return true;
    }

    public Task<Dictionary<string, string>> GetActiveWorkersAsync()
    {
        Dictionary<string, string> activeWorkers = new Dictionary<string, string>(_activeWorkers);

        return Task.FromResult(activeWorkers);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RestartWorkersForExistingTicketsAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await ShutdownAllWorkersAsync(cancellationToken);
    }

    // Stops all active worker containers during server shutdown.
    // Does NOT change ticket status — tickets remain in whatever state they were in
    // so they can be resumed when the server restarts.
    private async Task ShutdownAllWorkersAsync(CancellationToken cancellationToken)
    {
        List<(string TicketId, string ContainerId)> workers = new List<(string, string)>();
        foreach ((string ticketId, string containerId) in _activeWorkers)
        {
            workers.Add((ticketId, containerId));
        }

        if (workers.Count == 0)
        {
            _logger.LogInformation("Graceful shutdown: no active workers");
            return;
        }

        _logger.LogInformation("Graceful shutdown: stopping {Count} active workers", workers.Count);

        List<Task> stopTasks = new List<Task>();
        foreach ((string ticketId, string containerId) in workers)
        {
            stopTasks.Add(StopContainerForShutdownAsync(ticketId, containerId));
        }
        await Task.WhenAll(stopTasks);

        _activeWorkers.Clear();
        _logger.LogInformation("Graceful shutdown complete: all workers stopped");
    }

    // On startup, find all tickets that had a worker container and restart them.
    // Cleans up any stale containers left from a previous run first.
    private async Task RestartWorkersForExistingTicketsAsync(CancellationToken cancellationToken)
    {
        if (!_containerContext.IsRunningInDocker || _containerContext.Image == null)
        {
            _logger.LogInformation("Startup: not running in Docker, skipping worker restart");
            return;
        }

        IEnumerable<Ticket> allTickets = await _ticketService.GetAllTicketsAsync();

        List<Ticket> ticketsWithWorkers = new List<Ticket>();
        foreach (Ticket ticket in allTickets)
        {
            if (!string.IsNullOrEmpty(ticket.ContainerName) && ticket.Status != TicketStatus.Done)
            {
                ticketsWithWorkers.Add(ticket);
            }
        }

        if (ticketsWithWorkers.Count == 0)
        {
            _logger.LogInformation("Startup: no tickets need workers");
            return;
        }

        _logger.LogInformation("Startup: restarting workers for {Count} tickets", ticketsWithWorkers.Count);

        foreach (Ticket ticket in ticketsWithWorkers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                // Remove any stale container from a previous run.
                await RemoveStaleContainerAsync(ticket.ContainerName!);

                string containerName = await StartWorkerAsync(ticket.Id);
                _logger.LogInformation("Startup: restarted worker {Container} for ticket #{TicketId}", containerName, ticket.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Startup: failed to restart worker for ticket #{TicketId}: {Message}", ticket.Id, ex.Message);
            }
        }
    }

    // Tries to stop and remove a container by name if it still exists from a previous server run.
    private async Task RemoveStaleContainerAsync(string containerName)
    {
        try
        {
            IList<ContainerListResponse> containers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters { All = true });

            foreach (ContainerListResponse container in containers)
            {
                bool nameMatch = false;
                foreach (string name in container.Names)
                {
                    if (name == $"/{containerName}" || name == containerName)
                    {
                        nameMatch = true;
                        break;
                    }
                }

                if (nameMatch)
                {
                    _logger.LogInformation("Removing stale container {Name} ({Id})", containerName, container.ID);

                    try
                    {
                        await _dockerClient.Containers.StopContainerAsync(container.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 5 });
                    }
                    catch
                    {
                        // May already be stopped.
                    }

                    await _dockerClient.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { Force = true });
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to remove stale container {Name}: {Message}", containerName, ex.Message);
        }
    }

    private async Task StopContainerForShutdownAsync(string ticketId, string containerId)
    {
        try
        {
            _logger.LogInformation("Shutdown: stopping worker for ticket #{TicketId}", ticketId);
            await _dockerClient.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 30 });
            _logger.LogInformation("Shutdown: worker for ticket #{TicketId} stopped", ticketId);
        }
        catch (DockerContainerNotFoundException)
        {
            _logger.LogInformation("Worker container for ticket #{TicketId} already removed", ticketId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop worker for ticket #{TicketId} during shutdown: {Message}", ticketId, ex.Message);
        }
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
