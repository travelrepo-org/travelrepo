namespace TravelRepo.Merge;

/// <summary>Conservative three-way sequence merge using non-overlapping changes in base coordinates.</summary>
internal static class SequenceMerge
{
    private sealed record Edit(int Start, int End, string[] Values);
    public static bool TryMerge(string[] baseline, string[] current, string[] incoming, out string[] result)
    {
        result = [];
        // Bound memory for untrusted, unusually large content. The caller presents a reviewable conflict.
        if ((long)baseline.Length * Math.Max(current.Length, incoming.Length) > 4_000_000) return false;
        var left = Changes(baseline, current); var right = Changes(baseline, incoming);
        var edits = new List<Edit>(left);
        foreach (var r in right)
        {
            var duplicate = false;
            foreach (var l in left)
            {
                if (l.Start == r.Start && l.End == r.End && l.Values.SequenceEqual(r.Values)) { duplicate = true; break; }
                if (l.Start == r.Start || l.Start < r.End && r.Start < l.End || l.Start == l.End && l.Start > r.Start && l.Start < r.End || r.Start == r.End && r.Start > l.Start && r.Start < l.End) return false;
            }
            if (!duplicate) edits.Add(r);
        }
        var output = new List<string>(); var cursor = 0;
        foreach (var e in edits.OrderBy(e => e.Start)) { output.AddRange(baseline[cursor..e.Start]); output.AddRange(e.Values); cursor = e.End; }
        output.AddRange(baseline[cursor..]); result = output.ToArray(); return true;
    }
    private static List<Edit> Changes(string[] baseline, string[] changed)
    {
        var lcs = new int[baseline.Length + 1, changed.Length + 1];
        for (var x = baseline.Length - 1; x >= 0; x--) for (var y = changed.Length - 1; y >= 0; y--)
            lcs[x, y] = baseline[x] == changed[y] ? 1 + lcs[x + 1, y + 1] : Math.Max(lcs[x + 1, y], lcs[x, y + 1]);
        var result = new List<Edit>(); var i = 0; var j = 0;
        while (i < baseline.Length || j < changed.Length)
        {
            if (i < baseline.Length && j < changed.Length && baseline[i] == changed[j]) { i++; j++; continue; }
            var start = i; var values = new List<string>();
            while (i < baseline.Length || j < changed.Length)
            {
                if (i < baseline.Length && j < changed.Length && baseline[i] == changed[j]) break;
                if (j < changed.Length && (i == baseline.Length || lcs[i, j + 1] >= lcs[i + 1, j])) values.Add(changed[j++]); else i++;
            }
            result.Add(new(start, i, values.ToArray()));
        }
        return result;
    }
}
