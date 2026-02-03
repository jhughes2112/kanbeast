using Docker.DotNet;
using Docker.DotNet.Models;

namespace KanBeast.Server.Services;

// Provides container context derived from inspecting the current Docker container.
public class ContainerContext
{
    private const int DefaultServerPort = 8080;

    public string? ContainerId { get; private set; }
    public string? ContainerName { get; private set; }
    public string? Image { get; private set; }
    public string? Network { get; private set; }
    public string ServerUrl { get; private set; } = $"http://localhost:{DefaultServerPort}";
    public List<Mount> Mounts { get; private set; } = new List<Mount>();
    public bool IsRunningInDocker => ContainerId != null;

    public static async Task<ContainerContext> CreateAsync(ILogger<ContainerContext> logger)
    {
        ContainerContext context = new ContainerContext();
        await context.InitializeAsync(logger);
        return context;
    }

    private async Task InitializeAsync(ILogger<ContainerContext> logger)
    {
        ContainerId = GetCurrentContainerId();
        if (ContainerId == null)
        {
            logger.LogInformation("Not running in a container, using default configuration");
            return;
        }

        logger.LogInformation("Detected container ID: {ContainerId}", ContainerId);

        using DockerClient client = new DockerClientConfiguration().CreateClient();
        ContainerInspectResponse inspection = await client.Containers.InspectContainerAsync(ContainerId);

        // Extract container name (remove leading slash)
        ContainerName = inspection.Name;
        if (ContainerName != null && ContainerName.StartsWith("/"))
        {
            ContainerName = ContainerName.Substring(1);
        }

        // Extract image
        Image = inspection.Config?.Image;

        // Extract network (prefer first non-default network)
        if (inspection.NetworkSettings?.Networks != null)
        {
            foreach ((string networkName, EndpointSettings _) in inspection.NetworkSettings.Networks)
            {
                if (networkName != "bridge" && networkName != "host" && networkName != "none")
                {
                    Network = networkName;
                    break;
                }
            }

            // Fall back to first network if no custom network found
            if (Network == null)
            {
                foreach ((string networkName, EndpointSettings _) in inspection.NetworkSettings.Networks)
                {
                    Network = networkName;
                    break;
                }
            }
        }

        // Derive server URL from container name and exposed port
        int serverPort = GetExposedPort(inspection) ?? DefaultServerPort;
        if (ContainerName != null)
        {
            ServerUrl = $"http://{ContainerName}:{serverPort}";
        }

        // Extract mounts (excluding Docker socket)
        if (inspection.Mounts != null)
        {
            foreach (MountPoint mp in inspection.Mounts)
            {
                if (mp.Destination == "/var/run/docker.sock")
                {
                    continue;
                }

                Mount mount = new Mount
                {
                    Type = mp.Type,
                    Source = mp.Source,
                    Target = mp.Destination,
                    ReadOnly = mp.RW == false
                };
                Mounts.Add(mount);
                logger.LogInformation("Found mount: {Source} -> {Target}", mp.Source, mp.Destination);
            }
        }

        logger.LogInformation("Container context initialized: Name={Name}, Image={Image}, Network={Network}, ServerUrl={ServerUrl}",
            ContainerName, Image, Network, ServerUrl);
    }

    private static int? GetExposedPort(ContainerInspectResponse inspection)
    {
        if (inspection.Config?.ExposedPorts == null)
        {
            return null;
        }

        foreach ((string portSpec, EmptyStruct _) in inspection.Config.ExposedPorts)
        {
            // portSpec is like "8080/tcp"
            int slashIdx = portSpec.IndexOf('/');
            string portStr = slashIdx > 0 ? portSpec.Substring(0, slashIdx) : portSpec;
            if (int.TryParse(portStr, out int port))
            {
                return port;
            }
        }

        return null;
    }

    private static string? GetCurrentContainerId()
    {
        // Method 1: Check hostname (Docker sets container ID as hostname by default)
        string hostname = Environment.MachineName;
        if (hostname.Length == 12 && IsHexString(hostname))
        {
            return hostname;
        }

        // Method 2: Read from cgroup (Linux containers)
        string cgroupPath = "/proc/1/cpuset";
        if (File.Exists(cgroupPath))
        {
            string content = File.ReadAllText(cgroupPath).Trim();
            if (content.Contains("/docker/"))
            {
                int idx = content.LastIndexOf('/');
                if (idx >= 0 && idx < content.Length - 1)
                {
                    return content.Substring(idx + 1);
                }
            }
        }

        // Method 3: Check /.dockerenv file exists
        if (File.Exists("/.dockerenv"))
        {
            return hostname;
        }

        return null;
    }

    private static bool IsHexString(string value)
    {
        foreach (char c in value)
        {
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
