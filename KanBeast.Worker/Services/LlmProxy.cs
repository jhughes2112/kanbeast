using System.Text;
using KanBeast.Worker.Models;
using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Services;

// Coordinates multiple LLM endpoints with retry, fallback, and context handling.
public class LlmProxy : ILlmService
{
    private readonly List<LLMConfig> _configs;
    private readonly int _retryCount;
    private readonly TimeSpan _retryDelay;
    private readonly HashSet<int> _downedIndices;
    private readonly Dictionary<Kernel, KernelSet> _kernelSets;
    private readonly List<string> _contextStatements;
    private readonly ICompaction _compaction;
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
        _kernelSets = new Dictionary<Kernel, KernelSet>();
        _contextStatements = new List<string>();
        _compaction = compaction;

        Console.WriteLine($"LlmProxy initialized with {configs.Count} LLM config(s)");
        foreach (LLMConfig config in configs)
        {
            Console.WriteLine($"  - Model: {config.Model}, Endpoint: {config.Endpoint ?? "default"}, ContextLength: {config.ContextLength}");
        }
    }

    public Kernel CreateKernel(IEnumerable<object> tools)
    {
        KernelSet kernelSet = BuildKernelSet(tools);
        _kernelSets[kernelSet.PrimaryKernel] = kernelSet;

        Kernel kernel = kernelSet.PrimaryKernel;

        return kernel;
    }

    public async Task<string> RunAsync(Kernel kernel, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        if (!_kernelSets.TryGetValue(kernel, out KernelSet? kernelSet) || kernelSet == null)
        {
            throw new InvalidOperationException("Kernel is not registered with the LLM proxy.");
        }

        KernelSet resolvedKernelSet = kernelSet;
        string resolvedSystemPrompt = BuildSystemPrompt(systemPrompt);
        string result = string.Empty;
        bool succeeded = false;

        foreach (KernelEntry entry in resolvedKernelSet.Entries)
        {
            if (_downedIndices.Contains(entry.ConfigIndex))
            {
                continue;
            }

            int attempt = 0;
            while (attempt <= _retryCount && !succeeded)
            {
                try
                {
                    result = await entry.Service.RunAsync(entry.Kernel, resolvedSystemPrompt, userPrompt, cancellationToken);
                    succeeded = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"LLM {entry.ConfigIndex} attempt {attempt + 1} failed: {ex.Message}");
                    if (attempt < _retryCount)
                    {
                        if (_retryDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(_retryDelay, cancellationToken);
                        }
                    }
                    else
                    {
                        Console.WriteLine($"LLM {entry.ConfigIndex} marked as down after {_retryCount + 1} attempts");
                        _downedIndices.Add(entry.ConfigIndex);
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

    public Task ClearContextStatementsAsync(CancellationToken cancellationToken)
    {
        _contextStatements.Clear();

        return Task.CompletedTask;
    }

    public IReadOnlyList<string> GetContextStatements()
    {
        IReadOnlyList<string> statements = _contextStatements.AsReadOnly();

        return statements;
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

    private KernelSet BuildKernelSet(IEnumerable<object> tools)
    {
        List<KernelEntry> entries = new List<KernelEntry>();
        int index = 0;

        foreach (LLMConfig config in _configs)
        {
            LlmService service = new LlmService(config);
            service.LogDirectory = _logDirectory;
            service.LogPrefix = _logPrefix;
            Kernel kernel = service.CreateKernel(tools);

            KernelEntry entry = new KernelEntry(index, kernel, service);
            entries.Add(entry);
            index++;
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("No LLM configurations are available.");
        }

        KernelSet kernelSet = new KernelSet(entries);

        return kernelSet;
    }

    // Tracks kernels and services for each configured LLM.
    private sealed class KernelSet
    {
        public KernelSet(List<KernelEntry> entries)
        {
            Entries = entries;
            PrimaryKernel = entries[0].Kernel;
        }

        public Kernel PrimaryKernel { get; }

        public List<KernelEntry> Entries { get; }
    }

    // Binds a single LLM kernel to its config index for retry tracking.
    private sealed class KernelEntry
    {
        public KernelEntry(int configIndex, Kernel kernel, LlmService service)
        {
            ConfigIndex = configIndex;
            Kernel = kernel;
            Service = service;
        }

        public int ConfigIndex { get; }

        public Kernel Kernel { get; }

        public LlmService Service { get; }
    }
}
