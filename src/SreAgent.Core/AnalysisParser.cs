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
                        RawText: text);
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

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    private static IReadOnlyList<string> List(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return [];
        return v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0).ToList(),
            JsonValueKind.String => [v.GetString()!.Trim()],
            _ => [],
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
