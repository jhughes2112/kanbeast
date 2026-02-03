using System.Text;
using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Services;

// Defines the contract for context compaction strategies.
public interface ICompaction
{
    Task<List<string>> CompactAsync(List<string> contextStatements, CancellationToken cancellationToken);
}

// Keeps context statements untouched when compaction is disabled.
public class CompactionNone : ICompaction
{
    public Task<List<string>> CompactAsync(List<string> contextStatements, CancellationToken cancellationToken)
    {
        List<string> result = contextStatements;

        return Task.FromResult(result);
    }
}

// Summarizes context statements into a compact continuation summary.
public class CompactionSummarizer : ICompaction
{
    private const int MinimumThreshold = 3072;

    private readonly LlmService _summarizerService;
    private readonly Kernel _kernel;
    private readonly string _compactionPrompt;
    private readonly int _effectiveThreshold;

    public CompactionSummarizer(LlmService summarizerService, Kernel kernel, string compactionPrompt, int contextSizeThreshold, int llmContextLength)
    {
        _summarizerService = summarizerService;
        _kernel = kernel;
        _compactionPrompt = compactionPrompt;

        int llmLimit = (int)(llmContextLength * 0.9);
        if (contextSizeThreshold > 0)
        {
            _effectiveThreshold = Math.Max(MinimumThreshold, Math.Min(llmLimit, contextSizeThreshold));
        }
        else
        {
            _effectiveThreshold = Math.Max(MinimumThreshold, llmLimit);
        }
    }

    public async Task<List<string>> CompactAsync(List<string> contextStatements, CancellationToken cancellationToken)
    {
        List<string> compactedStatements = contextStatements;
        string contextBlock = BuildContextBlock(contextStatements);
        int contextSize = contextBlock.Length;

        if (contextSize > _effectiveThreshold)
        {
            string userPrompt = $"{_compactionPrompt}\n\nContext:\n{contextBlock}";
            string summary = await _summarizerService.RunAsync(_kernel, string.Empty, userPrompt, cancellationToken);
            List<string> summaryList = new List<string>();
            summaryList.Add(summary);
            compactedStatements = summaryList;
        }

        return compactedStatements;
    }

    private static string BuildContextBlock(IEnumerable<string> contextStatements)
    {
        StringBuilder builder = new StringBuilder();
        bool first = true;

        foreach (string statement in contextStatements)
        {
            if (!first)
            {
                builder.Append('\n');
            }

            builder.Append("- ");
            builder.Append(statement);
            first = false;
        }

        string content = builder.ToString();

        return content;
    }
}
