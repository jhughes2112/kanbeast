namespace KanBeast.Shared;

// Holds persistent memories that survive conversation resets and compaction.
// Shared across conversations so developer and QA see the same accumulated knowledge.
// Organized by label: INVARIANT, CONSTRAINT, DECISION, REFERENCE, OPEN_ITEM.
// Wraps ConversationData.Memories so changes are reflected in the serializable snapshot.
public class ConversationMemories
{
	private readonly Dictionary<string, List<string>> _memoriesByLabel;

	public Dictionary<string, List<string>> Backing => _memoriesByLabel;

	public ConversationMemories()
	{
		_memoriesByLabel = new Dictionary<string, List<string>>();
	}

	// Wraps an existing dictionary (e.g. from ConversationData.Memories).
	public ConversationMemories(Dictionary<string, List<string>> backing)
	{
		_memoriesByLabel = backing;
	}

	// Shares the same backing dictionary as the source.
	public ConversationMemories(ConversationMemories source)
	{
		_memoriesByLabel = source._memoriesByLabel;
	}

	public void Clear()
	{
		_memoriesByLabel.Clear();
	}

	public void Add(string label, string memory)
	{
		if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(memory))
		{
			return;
		}

		string normalizedLabel = label.Trim().ToUpperInvariant();
		string trimmedMemory = memory.Trim();

		if (!_memoriesByLabel.TryGetValue(normalizedLabel, out List<string>? list))
		{
			list = new List<string>();
			_memoriesByLabel[normalizedLabel] = list;
		}

		if (!list.Contains(trimmedMemory))
		{
			list.Add(trimmedMemory);
		}
	}

	// Tolerant of mistakes: finds the best match assuming it starts off similar.
	public bool Remove(string label, string memoryToRemove)
	{
		if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(memoryToRemove) || memoryToRemove.Trim().Length <= 5)
		{
			return false;
		}

		string normalizedLabel = label.Trim().ToUpperInvariant();

		if (!_memoriesByLabel.TryGetValue(normalizedLabel, out List<string>? memories))
		{
			return false;
		}

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
		foreach ((string label, List<string> memories) in _memoriesByLabel)
		{
			foreach (string m in memories)
			{
				sections += $"{label} {m}\n";
			}
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
