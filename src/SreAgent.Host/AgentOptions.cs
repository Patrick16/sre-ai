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
