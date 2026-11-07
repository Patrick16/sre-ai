using System.Text.Json;
using System.Text.RegularExpressions;

namespace SreAgent.Core;

/// <summary>
/// Turns the model's final reply into an <see cref="Analysis"/>. Small local models rarely return clean JSON:
/// they wrap it in ```json fences, add a &lt;think&gt; block or a sentence before it. Be lenient in what is accepted,
/// strict in what is used: anything that doesn't parse becomes Parsed=false, never an exception.
/// </summary>
public static partial class AnalysisParser
{
    public static Analysis Parse(string? text)
    {
        text ??= "";
        var cleaned = ThinkBlock().Replace(text, "");
        var start = cleaned.IndexOf('{');
        var end = cleaned.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var doc = JsonDocument.Parse(cleaned[start..(end + 1)]);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("hypothesis", out _))
                {
                    return new Analysis(
                        Str(root, "summary"),
                        Str(root, "hypothesis"),
                        NormalizeConfidence(Str(root, "confidence")),
                        List(root, "evidence"),
                        List(root, "recommendations"),
                        List(root, "next_checks"),
                        Parsed: true,
                        RawText: text)
                    { Facts = List(root, "facts") };
                }
            }
            catch (JsonException)
            {
                // fall through
            }
        }

        var preview = cleaned.Trim();
        if (preview.Length > 500) preview = preview[..500] + "…";
        return new Analysis(preview, "(the model did not return a structured answer)", "low", [], [], [], Parsed: false, RawText: text);
    }

    public static RunSynthesis? ParseSynthesis(string? text)
    {
        var cleaned = ThinkBlock().Replace(text ?? "", "");
        var start = cleaned.IndexOf('{');
        var end = cleaned.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(cleaned[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("root_cause", out _)) return null;
            return new RunSynthesis(
                Str(root, "summary"),
                Str(root, "root_cause"),
                NormalizeConfidence(Str(root, "confidence")),
                List(root, "timeline"),
                List(root, "evidence"),
                List(root, "recommendations"),
                Str(root, "disagreements"),
                Parsed: true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    private static IReadOnlyList<string> List(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return [];
        return v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray().Select(ItemText).Where(s => s.Length > 0).ToList(),
            JsonValueKind.String => [v.GetString()!.Trim()],
            _ => [],
        };
    }

    // Models sometimes answer ["a", "b"] and sometimes [{"fact": "a", "evidence": "b"}]: accept both.
    private static string ItemText(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) return e.GetString()!.Trim();
        if (e.ValueKind != JsonValueKind.Object) return "";
        var parts = e.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
            .Select(p => p.Value.GetString()!.Trim()).Where(s => s.Length > 0).ToList();
        return parts.Count switch
        {
            0 => "",
            1 => parts[0],
            _ => $"{parts[0]} ({string.Join("; ", parts.Skip(1))})",
        };
    }

    private static string NormalizeConfidence(string c) => c.ToLowerInvariant() switch
    {
        "high" or "medium" or "low" => c.ToLowerInvariant(),
        _ => "low",
    };

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();
}
