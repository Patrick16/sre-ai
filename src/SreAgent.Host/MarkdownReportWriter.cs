using System.Text;
using System.Text.RegularExpressions;
using SreAgent.Core;

namespace SreAgent.Host;

/// <summary>
/// Human-readable report of one run (docs/08). Deterministic sections (trigger, iterations, tool calls, timeline)
/// come from the recorded run; only Summary/Analysis/Recommendations come from the model - so a report cannot
/// claim actions or checks that never happened.
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
        sb.AppendLine($"- Actions: {RalfIteration.ObserveOnlyLead}");
        sb.AppendLine();

        if (last is not null)
        {
            var a = last.Analysis;
            sb.AppendLine("## Кратко");
            sb.AppendLine(a.Summary);
            sb.AppendLine();
            sb.AppendLine("## Анализ (последняя итерация)");
            sb.AppendLine($"**Гипотеза:** {a.Hypothesis} _(уверенность: {a.Confidence}{(a.Parsed ? "" : ", ответ модели не был структурирован")})_");
            sb.AppendLine();
            if (a.Evidence.Count > 0)
            {
                sb.AppendLine("**Факты:**");
                foreach (var e in a.Evidence) sb.AppendLine($"- {e}");
                sb.AppendLine();
            }
            sb.AppendLine("## Рекомендации");
            foreach (var r in a.Recommendations.DefaultIfEmpty("—")) sb.AppendLine($"- {r}");
            sb.AppendLine();
        }

        sb.AppendLine("## Итерации");
        sb.AppendLine("| # | Время | Длительность | Гипотеза | Уверенность | Tools | Completion check |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var it in run.Iterations)
            sb.AppendLine($"| {it.Number} | {it.StartedAt:HH:mm:ss}Z | {it.Duration.TotalSeconds:0}s | {Cell(it.Analysis.Hypothesis)} | {it.Analysis.Confidence} | {it.ToolCalls.Count} | {(it.Completion.Done ? "✅" : "⏳")} {Cell(it.Completion.Reason)} |");
        sb.AppendLine();

        sb.AppendLine("## Вызовы tools");
        foreach (var it in run.Iterations)
            foreach (var c in it.ToolCalls)
                sb.AppendLine($"- iteration {it.Number}: `{c.Tool}` {c.Arguments}{(c.Failed ? " — **failed**" : "")} ({c.Duration.TotalMilliseconds:0} ms)");
        if (!run.Iterations.Any(i => i.ToolCalls.Count > 0)) sb.AppendLine("—");
        sb.AppendLine();

        if (last is not null)
        {
            sb.AppendLine("## Наблюдения последней итерации (собраны автоматически)");
            sb.AppendLine("```text");
            sb.AppendLine(last.Observations);
            sb.AppendLine("```");
        }

        var dir = Path.Combine(directory, run.StartedAt.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{run.StartedAt:HHmmss}-{run.Task.Trigger.Kind.ToString().ToLowerInvariant()}-{Slug(title)}.md");
        await File.WriteAllTextAsync(path, sb.ToString(), ct);
        return path;
    }

    private static string Cell(string s) => s.Replace("|", "\\|").Replace("\n", " ");

    private static string Slug(string s)
    {
        var slug = NonWord().Replace(s.ToLowerInvariant(), "-").Trim('-');
        return slug.Length > 50 ? slug[..50] : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWord();
}
