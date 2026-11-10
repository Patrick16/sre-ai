using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using SreAgent.Core;

namespace SreAgent.LlmClients;

public sealed class OpenAiCompatibleChatClientFactory(IOptions<LlmOptions> options, ILoggerFactory loggerFactory) : IChatClientFactory
{
    /// <summary>ActivitySource/Meter name of the GenAI telemetry (token usage, request duration per model).</summary>
    public const string TelemetrySource = "SreAgent.Llm";

    private readonly LlmOptions _options = options.Value;

    // One client per model for the life of the agent: Create() is called every iteration and for every synthesis, and a
    // new OpenAIClient each time means a new connection pipeline (sockets in TIME_WAIT). The clients are stateless.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IChatClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Models =>
        _options.AllowedModels.Count > 0 ? _options.AllowedModels.Union([_options.Model]).ToList() : [_options.Model];

    public (IChatClient Client, string Model) Create(string? model)
    {
        var chosen = string.IsNullOrWhiteSpace(model) ? _options.Model : model.Trim();
        if (!Models.Contains(chosen, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Model '{chosen}' is not allowed. Allowed: {string.Join(", ", Models)}");

        return (_clients.GetOrAdd(chosen, Build), chosen);
    }

    private IChatClient Build(string chosen)
    {
        var openAi = new OpenAIClient(
            new ApiKeyCredential(string.IsNullOrEmpty(_options.ApiKey) ? "not-needed" : _options.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(_options.BaseUrl),
                NetworkTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
            });

        var client = new ErrorDetailsChatClient(openAi.GetChatClient(chosen).AsIChatClient())
            .AsBuilder()
            // gen_ai.* metrics and traces (OpenTelemetry GenAI semantic conventions). Prompts are NOT recorded.
            .UseOpenTelemetry(loggerFactory, TelemetrySource, t => t.EnableSensitiveData = false)
            .Build();
        return client;
    }
}
