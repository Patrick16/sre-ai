using SreAgent.Core;

namespace SreAgent.Host;

public sealed class AgentOptions
{
    public const string Section = "Agent";

    /// <summary>The namespace this agent looks after.</summary>
    public string Namespace { get; set; } = "sandbox";

    /// <summary>Reports, audit log and RunState files (a PVC in the cluster).</summary>
    public string DataDir { get; set; } = "/data";

    /// <summary>Periodic health check every N minutes; 0 = off (each run costs LLM time).</summary>
    public int ScheduleMinutes { get; set; }

    /// <summary>Language of the model's summary/hypothesis/recommendations in reports.</summary>
    public string Language { get; set; } = "Russian";

    /// <summary>Alert severities that start a run.</summary>
    public List<string> Severities { get; set; } = ["critical", "warning"];

    /// <summary>
    /// After a run started for an alert (by fingerprint), the same still-firing alert does not start another one for
    /// this many minutes (Alertmanager repeats it every hour). A resolved alert is forgotten at once. 0 = off.
    /// </summary>
    public int AlertSuppressionMinutes { get; set; } = 240;

    /// <summary>RunState files older than this many days are deleted at startup (reports and audit stay). 0 = keep all.</summary>
    public int StateRetentionDays { get; set; } = 14;

    /// <summary>
    /// Shared secret for the endpoints that change something (POST /runs, POST /approvals/{id}): sent as the header
    /// <c>X-Agent-Token</c>. Empty = not required (then only the NetworkPolicy protects them).
    /// </summary>
    public string ApiToken { get; set; } = "";

    public LimitsOptions Limits { get; set; } = new();

    public sealed class LimitsOptions
    {
        public int MaxIterations { get; set; } = 6;
        public int MaxWallTimeMinutes { get; set; } = 45;
        public int MaxToolCallsPerIteration { get; set; } = 6;
        public int BackoffSeconds { get; set; } = 60;
        public int MaxToolOutputChars { get; set; } = 4000;

        public AgentLimits ToLimits() => new()
        {
            MaxIterations = MaxIterations,
            MaxWallTime = TimeSpan.FromMinutes(MaxWallTimeMinutes),
            MaxToolCallsPerIteration = MaxToolCallsPerIteration,
            Backoff = TimeSpan.FromSeconds(BackoffSeconds),
            MaxToolOutputChars = MaxToolOutputChars,
        };
    }
}
