using System.Text;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Coordinates multiple LLM endpoints with retry, fallback, and context handling.
public class LlmProxy : ILlmService
{
    private readonly List<LLMConfig> _configs;
    private readonly int _retryCount;
    private readonly TimeSpan _retryDelay;
    private readonly HashSet<int> _downedIndices;
    private readonly List<string> _contextStatements;
    private readonly ICompaction _compaction;
    private List<IToolProvider> _providers;
    private LlmRole _role;
    private string _logDirectory = string.Empty;
    private string _logPrefix = string.Empty;

    public string LogDirectory
    {
        get => _logDirectory;
        set => _logDirectory = value;
    }

    public string LogPrefix
    {
        get => _logPrefix;
        set => _logPrefix = value;
    }

    public LlmProxy(List<LLMConfig> configs, int retryCount, int retryDelaySeconds, HashSet<int> downedIndices, ICompaction compaction)
    {
        _configs = configs;
        _retryCount = retryCount;
        _retryDelay = TimeSpan.FromSeconds(retryDelaySeconds);
        _downedIndices = downedIndices;
        _contextStatements = new List<string>();
        _compaction = compaction;
        _providers = new List<IToolProvider>();
        _role = LlmRole.Developer;

        Console.WriteLine($"LlmProxy initialized with {configs.Count} LLM config(s)");
        foreach (LLMConfig config in configs)
        {
            Console.WriteLine($"  - Model: {config.Model}, Endpoint: {config.Endpoint ?? "default"}, ContextLength: {config.ContextLength}");
        }
    }

    public void RegisterToolsFromProviders(IEnumerable<IToolProvider> providers, LlmRole role)
    {
        _providers = new List<IToolProvider>(providers);
        _role = role;
    }

    public async Task<string> RunAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        string resolvedSystemPrompt = BuildSystemPrompt(systemPrompt);
        string result = string.Empty;
        bool succeeded = false;

        for (int configIndex = 0; configIndex < _configs.Count; configIndex++)
        {
            if (_downedIndices.Contains(configIndex))
            {
                continue;
            }

            LLMConfig config = _configs[configIndex];
            LlmService service = new LlmService(config);
            service.LogDirectory = _logDirectory;
            service.LogPrefix = _logPrefix;
            service.RegisterToolsFromProviders(_providers, _role);

            int attempt = 0;
            while (attempt <= _retryCount && !succeeded)
            {
                try
                {
                    result = await service.RunAsync(resolvedSystemPrompt, userPrompt, cancellationToken);
                    succeeded = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"LLM {configIndex} attempt {attempt + 1} failed: {ex.Message}");
                    if (attempt < _retryCount)
                    {
                        if (_retryDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(_retryDelay, cancellationToken);
                        }
                    }
                    else
                    {
                        Console.WriteLine($"LLM {configIndex} marked as down after {_retryCount + 1} attempts");
                        _downedIndices.Add(configIndex);
                    }
                }

                attempt++;
            }

            if (succeeded)
            {
                break;
            }
        }

        if (!succeeded)
        {
            throw new InvalidOperationException("All configured LLMs are unavailable.");
        }

        return result;
    }

    public async Task AddContextStatementAsync(string statement, CancellationToken cancellationToken)
    {
        _contextStatements.Add(statement);

        List<string> workingStatements = new List<string>(_contextStatements);
        List<string> compactedStatements = await _compaction.CompactAsync(workingStatements, cancellationToken);

        _contextStatements.Clear();
        foreach (string compactedStatement in compactedStatements)
        {
            _contextStatements.Add(compactedStatement);
        }
    }

    public void ClearContextStatements()
    {
        _contextStatements.Clear();
    }

    private string BuildSystemPrompt(string systemPrompt)
    {
        string resolvedPrompt = systemPrompt;

        if (_contextStatements.Count > 0)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(systemPrompt);
            builder.Append("\n\nContext:\n");

            foreach (string statement in _contextStatements)
            {
                builder.Append("- ");
                builder.Append(statement);
                builder.Append('\n');
            }

            resolvedPrompt = builder.ToString().TrimEnd();
        }

        return resolvedPrompt;
    }
}
