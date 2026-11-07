namespace SreAgent.Tools;

public sealed class ToolsOptions
{
    public const string Section = "Tools";

    /// <summary>Namespaces the Kubernetes tools may look into. Anything else is refused.</summary>
    public List<string> Namespaces { get; set; } = ["sandbox"];

    public string PrometheusUrl { get; set; } = "http://kube-prometheus-stack-prometheus.monitoring.svc.cluster.local:9090";
    public string LokiUrl { get; set; } = "http://loki.monitoring.svc.cluster.local:3100";
    public string AlertmanagerUrl { get; set; } = "http://kube-prometheus-stack-alertmanager.monitoring.svc.cluster.local:9093";

    public void EnsureAllowed(string ns)
    {
        if (!Namespaces.Contains(ns, StringComparer.Ordinal))
            throw new ArgumentException($"namespace '{ns}' is outside the agent's scope; allowed: {string.Join(", ", Namespaces)}");
    }
}
