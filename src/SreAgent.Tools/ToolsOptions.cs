namespace SreAgent.Tools;

public sealed partial class ToolsOptions
{
    public const string Section = "Tools";

    /// <summary>Namespaces the Kubernetes tools may look into. Anything else is refused.</summary>
    public List<string> Namespaces { get; set; } = ["sandbox"];

    public string PrometheusUrl { get; set; } = "http://kube-prometheus-stack-prometheus.monitoring.svc.cluster.local:9090";
    public string LokiUrl { get; set; } = "http://loki.monitoring.svc.cluster.local:3100";
    public string AlertmanagerUrl { get; set; } = "http://kube-prometheus-stack-alertmanager.monitoring.svc.cluster.local:9093";

    /// <summary>Where the agent itself runs: the kill-switch ConfigMap lives there.</summary>
    public string AgentNamespace { get; set; } = "sre-agent";
    public string KillSwitchConfigMap { get; set; } = "sre-agent-kill-switch";

    public void EnsureAllowed(string ns)
    {
        if (!Namespaces.Contains(ns, StringComparer.Ordinal))
            throw new ArgumentException($"namespace '{ns}' is outside the agent's scope; allowed: {string.Join(", ", Namespaces)}");
    }

    /// <summary>
    /// A PromQL query may only name allowed namespaces in its <c>namespace</c> matchers. Prometheus has no per-query
    /// scoping, so this is a guard against the model wandering (or being talked into it by log text), not a security
    /// boundary: a query WITHOUT a namespace matcher still sees every namespace's metrics (read-only).
    /// </summary>
    public void EnsurePromqlInScope(string promql)
    {
        foreach (System.Text.RegularExpressions.Match m in NamespaceMatcher().Matches(promql))
        {
            var op = m.Groups["op"].Value;
            var value = m.Groups["value"].Value;
            // For regex matchers every alternative must be allowed; != and !~ only exclude, they never widen the scope.
            if (op is "!=" or "!~") continue;
            var alternatives = op == "=~" ? value.Split('|') : [value];
            foreach (var ns in alternatives) EnsureAllowed(ns);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"namespace\s*(?<op>=~|!~|!=|=)\s*""(?<value>[^""]*)""")]
    private static partial System.Text.RegularExpressions.Regex NamespaceMatcher();

    /// <summary>Label values go into LogQL: only plain names, so they cannot break out of the quotes.</summary>
    public static bool IsPlainLabelValue(string value) => PlainLabel().IsMatch(value);

    /// <summary>A regex of log levels such as "Warning|Error": letters and | only.</summary>
    public static bool IsLevelRegex(string value) => LevelRegex().IsMatch(value);

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9._-]{1,63}$")]
    private static partial System.Text.RegularExpressions.Regex PlainLabel();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z]+(\|[A-Za-z]+)*$")]
    private static partial System.Text.RegularExpressions.Regex LevelRegex();
}
