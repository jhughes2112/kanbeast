namespace KanBeast.Worker.Services;

// Holds persistent memories that survive conversation resets and compaction.
// Shared across conversations so developer and QA see the same accumulated knowledge.
public class LlmMemories
{
    private readonly List<string> _entries = new();

    public IReadOnlyList<string> Entries => _entries;

    public void Add(string memory)
    {
        if (string.IsNullOrWhiteSpace(memory))
        {
            return;
        }

        _entries.Add(memory.Trim());
    }

    // Tolerant of mistakes: finds the best match assuming it starts off similar.
    public bool Remove(string memoryToRemove)
    {
        if (string.IsNullOrWhiteSpace(memoryToRemove) || memoryToRemove.Trim().Length <= 5)
        {
            return false;
        }

        string searchText = memoryToRemove.Trim();
        int bestMatchIndex = -1;
        int bestMatchLength = 0;

        for (int i = 0; i < _entries.Count; i++)
        {
            string entry = _entries[i].Trim();
            int matchLength = GetCommonPrefixLength(entry, searchText);
            if (matchLength > bestMatchLength)
            {
                bestMatchLength = matchLength;
                bestMatchIndex = i;
            }
        }

        if (bestMatchIndex >= 0 && bestMatchLength > 5)
        {
            _entries.RemoveAt(bestMatchIndex);
            return true;
        }

        return false;
    }

    public string FormatForPrompt()
    {
        if (_entries.Count == 0)
        {
            return "[Memories: None yet]";
        }

        string joined = string.Join("\n", _entries.Select(m => $"- {m}"));
        return $"[Memories]\n{joined}";
    }

    public string FormatForCompaction()
    {
        if (_entries.Count == 0)
        {
            return "[Current memories]\nNone";
        }

        string joined = string.Join("\n", _entries.Select(m => $"- {m}"));
        return $"[Current memories]\n{joined}";
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
