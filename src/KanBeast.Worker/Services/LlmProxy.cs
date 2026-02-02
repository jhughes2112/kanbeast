using KanBeast.Worker.Models;
using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Services;

public class LlmProxy : ILlmService
{
    private readonly List<LLMConfig> _configs;
    private readonly int _retryCount;
    private readonly TimeSpan _retryDelay;
    private readonly HashSet<int> _downedIndices;
    private readonly Dictionary<Kernel, KernelSet> _kernelSets;

    public LlmProxy(List<LLMConfig> configs, int retryCount, int retryDelaySeconds)
    {
        _configs = configs;
        _retryCount = retryCount;
        _retryDelay = TimeSpan.FromSeconds(retryDelaySeconds);
        _downedIndices = new HashSet<int>();
        _kernelSets = new Dictionary<Kernel, KernelSet>();
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
                    result = await entry.Service.RunAsync(entry.Kernel, systemPrompt, userPrompt, cancellationToken);
                    succeeded = true;
                }
                catch (Exception)
                {
                    if (attempt < _retryCount)
                    {
                        if (_retryDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(_retryDelay, cancellationToken);
                        }
                    }
                    else
                    {
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

    private KernelSet BuildKernelSet(IEnumerable<object> tools)
    {
        List<KernelEntry> entries = new List<KernelEntry>();
        int index = 0;

        foreach (LLMConfig config in _configs)
        {
            LlmService service = new LlmService(config);
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
