using CommandLine;

namespace KanBeast.Server.Services;

public class ServerOptions
{
    [Option("worker-image", HelpText = "Docker image used for worker containers.")]
    public string WorkerImage { get; set; } = "kanbeast";

    [Option("docker-network", HelpText = "Docker network used for worker containers.")]
    public string DockerNetwork { get; set; } = "kanbeast-network";

    [Option("server-url", HelpText = "Server URL accessible from worker containers.")]
    public string ServerUrl { get; set; } = "http://kanbeast-server:8080";

}
