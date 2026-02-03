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
    private readonly LlmService _summarizerService;
    private readonly Kernel _kernel;
    private readonly string _summaryPrompt;
    private readonly string _summarySystemPrompt;
    private readonly int _contextSizeThreshold;

    public CompactionSummarizer(LlmService summarizerService, Kernel kernel, string summaryPrompt, string summarySystemPrompt, int contextSizeThreshold)
    {
        _summarizerService = summarizerService;
        _kernel = kernel;
        _summaryPrompt = summaryPrompt;
        _summarySystemPrompt = summarySystemPrompt;
        _contextSizeThreshold = contextSizeThreshold;
    }

    public async Task<List<string>> CompactAsync(List<string> contextStatements, CancellationToken cancellationToken)
    {
        List<string> compactedStatements = contextStatements;
        string contextBlock = BuildContextBlock(contextStatements);
        int contextSize = contextBlock.Length;

        if (contextSize > _contextSizeThreshold)
        {
            string userPrompt = $"{_summaryPrompt}\n\nContext:\n{contextBlock}";
            string summary = await _summarizerService.RunAsync(_kernel, _summarySystemPrompt, userPrompt, cancellationToken);
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
