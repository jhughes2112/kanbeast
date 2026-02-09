namespace KanBeast.Worker.Services;

// Holds persistent memories that survive conversation resets and compaction.
// Shared across conversations so developer and QA see the same accumulated knowledge.
// Organized by label: INVARIANT, CONSTRAINT, DECISION, REFERENCE, OPEN_ITEM.
public class LlmMemories
{
    private readonly Dictionary<string, HashSet<string>> _memoriesByLabel = new();

    public IReadOnlyDictionary<string, HashSet<string>> MemoriesByLabel => _memoriesByLabel;

    public void Add(string label, string memory)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(memory))
        {
            return;
        }

        string normalizedLabel = label.Trim().ToUpperInvariant();
        string trimmedMemory = memory.Trim();

        if (!_memoriesByLabel.ContainsKey(normalizedLabel))
        {
            _memoriesByLabel[normalizedLabel] = new HashSet<string>();
        }

        _memoriesByLabel[normalizedLabel].Add(trimmedMemory);
    }

    // Tolerant of mistakes: finds the best match assuming it starts off similar.
    public bool Remove(string label, string memoryToRemove)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(memoryToRemove) || memoryToRemove.Trim().Length <= 5)
        {
            return false;
        }

        string normalizedLabel = label.Trim().ToUpperInvariant();

        if (!_memoriesByLabel.ContainsKey(normalizedLabel))
        {
            return false;
        }

        HashSet<string> memories = _memoriesByLabel[normalizedLabel];
        string searchText = memoryToRemove.Trim();
        string? bestMatch = null;
        int bestMatchLength = 0;

        foreach (string entry in memories)
        {
            int matchLength = GetCommonPrefixLength(entry, searchText);
            if (matchLength > bestMatchLength)
            {
                bestMatchLength = matchLength;
                bestMatch = entry;
            }
        }

        if (bestMatch != null && bestMatchLength > 5)
        {
            memories.Remove(bestMatch);
            if (memories.Count == 0)
            {
                _memoriesByLabel.Remove(normalizedLabel);
            }
            return true;
        }

        return false;
    }

    public string Format()
    {
        if (_memoriesByLabel.Count == 0)
        {
            return "[Memories: None yet]";
        }

        string sections = "[Memories]\n";
        foreach ((string label, HashSet<string> memories) in _memoriesByLabel)
        {
            sections += string.Join("\n", memories.Select(m => $"{label} {m}"));
        }

        return sections;
    }

    private static int GetCommonPrefixLength(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return 0;
        }

        int length = 0;
        int maxLength = Math.Min(a.Length, b.Length);
        while (length < maxLength && a[length] == b[length])
        {
            length++;
        }

        return length;
    }
}
