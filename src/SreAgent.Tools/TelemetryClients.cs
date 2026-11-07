using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SreAgent.Tools;

public sealed record PromSample(IReadOnlyDictionary<string, string> Labels, double Value);

/// <summary>Prometheus HTTP API (instant queries). Errors are returned as data, not thrown, so the model sees them.</summary>
public sealed class PrometheusClient(HttpClient http)
{
    public async Task<(IReadOnlyList<PromSample> Samples, string? Error)> QueryAsync(string promql, CancellationToken ct)
    {
        using var response = await http.GetAsync($"/api/v1/query?query={Uri.EscapeDataString(promql)}", ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.GetProperty("status").GetString() != "success")
            return ([], $"{root.GetProperty("errorType").GetString()}: {root.GetProperty("error").GetString()}");

        var data = root.GetProperty("data");
        var samples = new List<PromSample>();
        if (data.GetProperty("resultType").GetString() == "scalar")
        {
            samples.Add(new PromSample(new Dictionary<string, string>(), ParseValue(data.GetProperty("result"))));
            return (samples, null);
        }
        foreach (var r in data.GetProperty("result").EnumerateArray())
        {
            var labels = r.GetProperty("metric").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
            if (r.TryGetProperty("value", out var v)) samples.Add(new PromSample(labels, ParseValue(v)));
        }
        return (samples, null);
    }

    public static string Format(IReadOnlyList<PromSample> samples, int max = 25)
    {
        if (samples.Count == 0) return "(no series: the query matched nothing)";
        var sb = new StringBuilder();
        foreach (var s in samples.OrderByDescending(s => s.Value).Take(max))
        {
            var labels = string.Join(", ", s.Labels.Where(l => l.Key is not ("__name__" or "endpoint" or "instance" or "job" or "container" or "otel_scope_name" or "prometheus" or "service"))
                .Select(l => $"{l.Key}={l.Value}"));
            sb.AppendLine($"{{{labels}}} = {s.Value.ToString("0.####", CultureInfo.InvariantCulture)}");
        }
        if (samples.Count > max) sb.AppendLine($"…and {samples.Count - max} more series");
        return sb.ToString().TrimEnd();
    }

    private static double ParseValue(JsonElement pair) =>
        double.Parse(pair[1].GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
}

public sealed record LogLine(DateTimeOffset At, string Pod, string Level, string Message);

/// <summary>Loki query_range API. Our .NET services log JSON; the Message field is what a human wants to read.</summary>
public sealed class LokiClient(HttpClient http)
{
    public async Task<IReadOnlyList<LogLine>> QueryAsync(string logql, TimeSpan window, int limit, CancellationToken ct)
    {
        var end = DateTimeOffset.UtcNow;
        var url = $"/loki/api/v1/query_range?query={Uri.EscapeDataString(logql)}&limit={limit}&direction=backward" +
                  $"&start={(end - window).ToUnixTimeMilliseconds()}000000&end={end.ToUnixTimeMilliseconds()}000000";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        var lines = new List<LogLine>();
        foreach (var stream in doc.RootElement.GetProperty("data").GetProperty("result").EnumerateArray())
        {
            var labels = stream.GetProperty("stream");
            var pod = labels.TryGetProperty("pod", out var p) ? p.GetString() ?? "" : "";
            var level = labels.TryGetProperty("level", out var l) ? l.GetString() ?? "" : "";
            foreach (var v in stream.GetProperty("values").EnumerateArray())
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(v[0].GetString()!) / 1_000_000);
                lines.Add(new LogLine(at, pod, level, MessageOf(v[1].GetString() ?? "")));
            }
        }
        return lines.OrderByDescending(l => l.At).Take(limit).ToList();
    }

    private static string MessageOf(string line)
    {
        if (line.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("Message", out var m)) line = m.GetString() ?? line;
            }
            catch (JsonException) { }
        }
        return line.Length > 300 ? line[..300] + "…" : line;
    }
}

public sealed record ActiveAlert(string Name, string Severity, string? Namespace, string? Summary, string Fingerprint, DateTimeOffset StartsAt, bool IsSloAlert);

/// <summary>Alertmanager v2 API: what is firing right now (not silenced, not inhibited).</summary>
public sealed class AlertmanagerClient(HttpClient http)
{
    public async Task<IReadOnlyList<ActiveAlert>> ActiveAsync(CancellationToken ct)
    {
        var alerts = await http.GetFromJsonAsync<JsonElement>("/api/v2/alerts?active=true&silenced=false&inhibited=false", ct);
        var result = new List<ActiveAlert>();
        foreach (var a in alerts.EnumerateArray())
        {
            var labels = a.GetProperty("labels");
            string? Label(string n) => labels.TryGetProperty(n, out var v) ? v.GetString() : null;
            var summary = a.GetProperty("annotations").TryGetProperty("summary", out var s) ? s.GetString() : null;
            var name = Label("alertname") ?? "?";
            if (name is "Watchdog" or "InfoInhibitor") continue;
            result.Add(new ActiveAlert(name, Label("severity") ?? "none", Label("namespace"), summary,
                a.GetProperty("fingerprint").GetString() ?? "", a.GetProperty("startsAt").GetDateTimeOffset(),
                IsSloAlert: Label("slo") is not null));
        }
        return result;
    }
}
