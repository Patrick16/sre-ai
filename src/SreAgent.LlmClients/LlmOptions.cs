namespace SreAgent.LlmClients;

/// <summary>
/// Any OpenAI-compatible endpoint: LM Studio on this machine, Ollama, vLLM, or a cloud provider.
/// They differ only in BaseUrl, ApiKey and model name - the protocol (/v1/chat/completions + tool calls) is the same.
/// The top-level settings describe the built-in provider (<see cref="Name"/>, LM Studio by default);
/// <see cref="Providers"/> adds more. Where the agent may connect is decided HERE (and by the NetworkPolicy), never by
/// a request: a caller picks a provider by name, it cannot supply a URL.
/// </summary>
public sealed class LlmOptions
{
    public const string Section = "Llm";

    /// <summary>Name of the built-in provider in the API and the console.</summary>
    public string Name { get; set; } = "local";

    /// <summary>From a pod in kind, LM Studio on the Windows host is http://host.docker.internal:1234/v1.</summary>
    public string BaseUrl { get; set; } = "http://host.docker.internal:1234/v1";

    /// <summary>Default model; a run can ask for another one (see <see cref="AllowedModels"/>).</summary>
    public string Model { get; set; } = "qwen/qwen3.5-9b";

    /// <summary>Empty for LM Studio. For a cloud provider: from a Kubernetes Secret, never from a values file.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Local models on a desktop GPU can take minutes for a long prompt.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Models a run may switch to. Empty = only <see cref="Model"/>.</summary>
    public List<string> AllowedModels { get; set; } = [];

    /// <summary>
    /// More providers, by name: Llm__Providers__openrouter__BaseUrl=https://openrouter.ai/api/v1 ... Empty by default.
    /// A pod can only reach them if the NetworkPolicy allows the egress (see the sre-agent values file).
    /// </summary>
    public Dictionary<string, ProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public sealed class ProviderOptions
    {
        public string BaseUrl { get; set; } = "";

        /// <summary>From a Secret (secretEnv), or set at runtime through the API (kept in memory only).</summary>
        public string? ApiKey { get; set; }

        /// <summary>Models a run may use. Empty = any model id the provider accepts.</summary>
        public List<string> Models { get; set; } = [];

        /// <summary>False for key-less servers (Ollama, vLLM): then a run may start without a key.</summary>
        public bool RequiresKey { get; set; } = true;
    }
}
