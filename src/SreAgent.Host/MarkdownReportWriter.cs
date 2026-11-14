using System.Text;
using System.Text.RegularExpressions;
using SreAgent.Core;

namespace SreAgent.Host;

/// <summary>
/// Human-readable report of one run (docs/08). Deterministic sections (trigger, iterations, tool calls, timeline)
/// come from the recorded run; only Summary/Analysis/Recommendations come from the model - so a report cannot
/// claim actions or checks that never happened. The Actions section is written from the guardrails' records.
/// Headings are always English; the language of the model's text follows <c>Agent:Language</c>.
/// </summary>
public sealed partial class MarkdownReportWriter(string directory)
{
    public async Task<string> WriteAsync(AgentRun run, CancellationToken ct)
    {
        var last = run.Iterations.LastOrDefault();
        var title = run.Task.Trigger.Alerts.Count > 0
            ? string.Join(", ", run.Task.Trigger.Alerts.Select(a => a.Name).Distinct())
            : $"{run.Task.Trigger.Kind} check";
        var duration = (run.FinishedAt ?? run.StartedAt) - run.StartedAt;

        var sb = new StringBuilder();
        sb.AppendLine($"# [{run.Outcome}] {title} — {run.Task.Namespace}");
        sb.AppendLine();
        sb.AppendLine($"- Run: `{run.Task.Id}`, started {run.StartedAt:yyyy-MM-dd HH:mm:ss}Z, trigger: {run.Task.Trigger.Kind}");
        sb.AppendLine($"- Outcome: **{run.Outcome}** — {run.OutcomeReason}");
        sb.AppendLine($"- Ralph loop iterations: {run.Iterations.Count} of {run.Task.Limits.MaxIterations}, duration {duration:hh\\:mm\\:ss}");
        sb.AppendLine($"- Model: {last?.Model ?? "-"}, tokens in/out: {run.Iterations.Sum(i => i.InputTokens)}/{run.Iterations.Sum(i => i.OutputTokens)}, tool calls: {run.Iterations.Sum(i => i.ToolCalls.Count)}");
        var actions = run.Actions;
        sb.AppendLine(actions.Count == 0
            ? $"- Actions: none ({run.Iterations.LastOrDefault()?.Lead ?? "-"})"
            : $"- Actions: {actions.Count} proposed, {actions.Count(a => a.Executed)} executed ({string.Join(", ", actions.Select(a => $"{a.Proposal.Type} {a.Proposal.Target} → {a.Status}{(a.Executed ? $"/{a.Effect}" : "")}"))})");
        sb.AppendLine();

        if (run.Synthesis is { } s)
        {
            sb.AppendLine("## Summary");
            sb.AppendLine(s.Summary);
            sb.AppendLine();
            sb.AppendLine("## Root cause");
            sb.AppendLine($"**{s.RootCause}** _(confidence: {s.Confidence}; conclusion over all iterations — synthesized after the loop)_");
            sb.AppendLine();
            if (s.Timeline.Count > 0)
            {
                sb.AppendLine("## How the incident developed");
                foreach (var t in s.Timeline) sb.AppendLine($"- {t}");
                sb.AppendLine();
            }
            if (s.Evidence.Count > 0)
            {
                sb.AppendLine("**Evidence:**");
                foreach (var e in s.Evidence) sb.AppendLine($"- {e}");
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(s.Disagreements))
            {
                sb.AppendLine("## Disagreements between iterations");
                sb.AppendLine(s.Disagreements);
                sb.AppendLine();
            }
            sb.AppendLine("## Recommendations");
            foreach (var r in s.Recommendations.DefaultIfEmpty("—")) sb.AppendLine($"- {r}");
            sb.AppendLine();
        }
        else if (last is not null)
        {
            var a = last.Analysis;
            sb.AppendLine("## Summary");
            sb.AppendLine(a.Summary);
            sb.AppendLine();
            sb.AppendLine("## Analysis (last iteration — the synthesis over the whole run did not work)");
            sb.AppendLine($"**Hypothesis:** {a.Hypothesis} _(confidence: {a.Confidence}{(a.Parsed ? "" : ", the model's answer was not structured")})_");
            sb.AppendLine();
            if (a.Evidence.Count > 0)
            {
                sb.AppendLine("**Evidence:**");
                foreach (var e in a.Evidence) sb.AppendLine($"- {e}");
                sb.AppendLine();
            }
            sb.AppendLine("## Recommendations");
            foreach (var r in a.Recommendations.DefaultIfEmpty("—")) sb.AppendLine($"- {r}");
            sb.AppendLine();
        }

        if (actions.Count > 0)
        {
            // Deterministic: straight from what the guardrails and the executor recorded, never from the model's text.
            sb.AppendLine("## Actions (guardrail decisions, from the run record)");
            foreach (var a in actions)
            {
                sb.AppendLine($"### Iteration {a.Iteration}: `{a.Proposal.Type} {a.Proposal.Target}` — **{a.Status}** (mode {a.Mode})");
                sb.AppendLine($"- The model's reason: {a.Proposal.Reason}");
                sb.AppendLine($"- Result: {a.Detail}");
                if (a.ApprovedBy is not null) sb.AppendLine($"- Approved by: {a.ApprovedBy}");
                if (a.Executed) sb.AppendLine($"- Effect: **{a.Effect}** — before: {a.Before}; after: {a.After}");
                sb.AppendLine($"- Checks: {string.Join("; ", a.Checks.Select(c => $"{(c.Passed ? "✅" : "❌")} {c.Name}: {c.Detail}"))}");
                sb.AppendLine();
            }
        }

        if (run.Facts.Count > 0)
        {
            sb.AppendLine("## Established facts (verified during the run)");
            foreach (var f in run.Facts) sb.AppendLine($"- {f}");
            sb.AppendLine();
        }

        sb.AppendLine("## Iterations");
        sb.AppendLine("| # | Time | Duration | Hypothesis | Confidence | Tools | Lead | Completion check |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var it in run.Iterations)
            sb.AppendLine($"| {it.Number} | {it.StartedAt:HH:mm:ss}Z | {it.Duration.TotalSeconds:0}s | {Cell(it.Analysis.Hypothesis)} | {it.Analysis.Confidence} | {it.ToolCalls.Count} | {Cell(it.Action is { } act ? $"{act.Proposal.Type} {act.Proposal.Target}: {act.Status}{(act.Executed ? $"/{act.Effect}" : "")}" : "—")} | {(it.Completion.Done ? "✅" : "⏳")} {Cell(it.Completion.Reason)} |");
        sb.AppendLine();

        sb.AppendLine("## Tool calls");
        foreach (var it in run.Iterations)
            foreach (var c in it.ToolCalls)
                sb.AppendLine($"- iteration {it.Number}: `{c.Tool}` {c.Arguments}{(c.Failed ? " — **failed**" : "")} ({c.Duration.TotalMilliseconds:0} ms)");
        if (!run.Iterations.Any(i => i.ToolCalls.Count > 0)) sb.AppendLine("—");
        sb.AppendLine();

        if (last is not null)
        {
            sb.AppendLine("## Observations of the last iteration (collected automatically)");
            // The observations include log lines, which may contain ``` and would end the block early:
            // use a fence one backtick longer than the longest run of backticks inside.
            var fence = new string('`', Math.Max(3, LongestBacktickRun(last.Observations) + 1));
            sb.AppendLine($"{fence}text");
            sb.AppendLine(last.Observations);
            sb.AppendLine(fence);
        }

        var dir = Path.Combine(directory, run.StartedAt.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{run.StartedAt:HHmmss}-{run.Task.Trigger.Kind.ToString().ToLowerInvariant()}-{Slug(title)}.md");
        await File.WriteAllTextAsync(path, sb.ToString(), ct);
        return path;
    }

    private static string Cell(string s) => s.Replace("|", "\\|").Replace("\n", " ");

    internal static int LongestBacktickRun(string s)
    {
        int best = 0, current = 0;
        foreach (var c in s)
        {
            current = c == '`' ? current + 1 : 0;
            best = Math.Max(best, current);
        }
        return best;
    }

    /// <summary>File-name part of the title; never empty (a non-Latin alert name would otherwise give "...-.md").</summary>
    internal static string Slug(string s)
    {
        var slug = NonWord().Replace(s.ToLowerInvariant(), "-").Trim('-');
        var cut = slug.Length > 50 ? slug[..50].Trim('-') : slug;
        return cut.Length == 0 ? "report" : cut;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWord();
}
