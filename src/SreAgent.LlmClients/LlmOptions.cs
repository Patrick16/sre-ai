namespace SreAgent.LlmClients;

/// <summary>
/// Any OpenAI-compatible endpoint: LM Studio on this machine, Ollama, vLLM, or a cloud provider.
/// They differ only in BaseUrl, ApiKey and model name - the protocol (/v1/chat/completions + tool calls) is the same.
/// </summary>
public sealed class LlmOptions
{
    public const string Section = "Llm";

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
}
