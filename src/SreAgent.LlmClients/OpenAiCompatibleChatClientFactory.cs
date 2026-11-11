using System.ClientModel;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using SreAgent.Core;

namespace SreAgent.LlmClients;

/// <summary>What the API and the console may know about a provider. Never the key itself - only whether one is set.</summary>
public sealed record ProviderInfo(string Name, string BaseUrl, IReadOnlyList<string> Models, bool AnyModel, bool RequiresKey, bool KeySet, string KeySource);

/// <summary>
/// A run's choice of provider and model as ONE string, so SreAgent.Core keeps passing an opaque "model" around:
/// "provider::model", or just "model" for the default provider ("::" because model ids contain "/" and ":").
/// </summary>
public static class LlmRef
{
    private const string Separator = "::";

    public static string? Format(string? provider, string? model) =>
        string.IsNullOrWhiteSpace(provider) ? (string.IsNullOrWhiteSpace(model) ? null : model.Trim()) : $"{provider.Trim()}{Separator}{model?.Trim()}";

    public static (string? Provider, string? Model) Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return (null, null);
        var i = spec.IndexOf(Separator, StringComparison.Ordinal);
        if (i < 0) return (null, spec.Trim());
        var model = spec[(i + Separator.Length)..].Trim();
        return (spec[..i].Trim(), model.Length == 0 ? null : model);
    }
}

public sealed class OpenAiCompatibleChatClientFactory : IChatClientFactory
{
    /// <summary>ActivitySource/Meter name of the GenAI telemetry (token usage, request duration per model).</summary>
    public const string TelemetrySource = "SreAgent.Llm";

    private sealed record Provider(string Name, string BaseUrl, string? ConfiguredKey, IReadOnlyList<string> Models, bool AnyModel, bool RequiresKey, string? DefaultModel);

    private readonly ILoggerFactory _loggerFactory;
    private readonly int _timeoutSeconds;
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _builtIn;

    // Runtime choices made by a human through the API. In memory on purpose: a restart goes back to the configuration,
    // and a key typed into the console is never written to disk.
    private readonly ConcurrentDictionary<string, string> _runtimeKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _defaultLock = new();
    private string _defaultProvider;
    private string _defaultModel;

    // One client per provider+model for the life of the agent: Create() is called every iteration and for every
    // synthesis, and a new OpenAIClient each time means a new connection pipeline (sockets in TIME_WAIT). The clients
    // are stateless. A changed key drops that provider's clients.
    private readonly ConcurrentDictionary<(string Provider, string Model), IChatClient> _clients = new();

    public OpenAiCompatibleChatClientFactory(IOptions<LlmOptions> options, ILoggerFactory loggerFactory)
    {
        var o = options.Value;
        _loggerFactory = loggerFactory;
        _timeoutSeconds = o.TimeoutSeconds;
        _builtIn = o.Name;
        List<string> allowed = o.AllowedModels.Count > 0 ? o.AllowedModels.Union([o.Model], StringComparer.OrdinalIgnoreCase).ToList() : [o.Model];
        _providers[o.Name] = new Provider(o.Name, o.BaseUrl, o.ApiKey, allowed, AnyModel: false, RequiresKey: false, o.Model);
        foreach (var (name, p) in o.Providers)
        {
            if (string.IsNullOrWhiteSpace(p.BaseUrl) || _providers.ContainsKey(name)) continue;
            _providers[name] = new Provider(name, p.BaseUrl, p.ApiKey, p.Models, AnyModel: p.Models.Count == 0, p.RequiresKey, p.Models.FirstOrDefault());
        }
        _defaultProvider = o.Name;
        _defaultModel = o.Model;
    }

    /// <summary>Models of the built-in provider (kept for GET /models and older callers).</summary>
    public IReadOnlyList<string> Models => _providers[_builtIn].Models;

    public (string Provider, string Model) Default
    {
        get { lock (_defaultLock) return (_defaultProvider, _defaultModel); }
    }

    public IReadOnlyList<ProviderInfo> Providers => _providers.Values.Select(p =>
    {
        var runtime = _runtimeKeys.ContainsKey(p.Name);
        var configured = !string.IsNullOrEmpty(p.ConfiguredKey);
        return new ProviderInfo(p.Name, p.BaseUrl, p.Models, p.AnyModel, p.RequiresKey, runtime || configured,
            runtime ? "runtime" : configured ? "config" : "none");
    }).ToList();

    /// <summary>Checks a choice without building anything; the error text is meant for the person who made it.</summary>
    /// <returns>the resolved provider and model, or an error.</returns>
    public (string? Provider, string? Model, string? Error) Resolve(string? spec)
    {
        var (providerName, model) = LlmRef.Parse(spec);
        var (defaultProvider, defaultModel) = Default;
        providerName ??= defaultProvider;
        if (!_providers.TryGetValue(providerName, out var p))
            return (null, null, $"provider '{providerName}' is not configured. Configured: {string.Join(", ", _providers.Keys)}");

        model ??= p.Name.Equals(defaultProvider, StringComparison.OrdinalIgnoreCase) ? defaultModel : p.DefaultModel;
        if (string.IsNullOrWhiteSpace(model)) return (null, null, $"provider '{p.Name}' has no default model: choose one");
        if (!p.AnyModel && !p.Models.Contains(model, StringComparer.OrdinalIgnoreCase))
            return (null, null, $"model '{model}' is not allowed for provider '{p.Name}'. Allowed: {string.Join(", ", p.Models)}");
        if (p.RequiresKey && KeyFor(p) is null)
            return (null, null, $"provider '{p.Name}' needs an API key: set it in the console or in a Secret (Llm__Providers__{p.Name}__ApiKey)");
        return (p.Name, model, null);
    }

    public (IChatClient Client, string Model) Create(string? model)
    {
        var (provider, chosen, error) = Resolve(model);
        if (error is not null) throw new ArgumentException(error);
        var p = _providers[provider!];
        return (_clients.GetOrAdd((p.Name.ToLowerInvariant(), chosen!), _ => Build(p, chosen!)), chosen!);
    }

    /// <summary>The default for runs that don't choose (alerts, the schedule). Validated like a run's own choice.</summary>
    /// <returns>an error text, or null when the default was changed.</returns>
    public string? SetDefault(string? provider, string? model)
    {
        var (p, m, error) = Resolve(LlmRef.Format(provider ?? Default.Provider, model));
        if (error is not null) return error;
        lock (_defaultLock) (_defaultProvider, _defaultModel) = (p!, m!);
        return null;
    }

    /// <summary>Sets (or, with an empty key, forgets) a provider's runtime key. False when the provider doesn't exist.</summary>
    public bool SetKey(string provider, string? apiKey)
    {
        if (!_providers.TryGetValue(provider, out var p)) return false;
        if (string.IsNullOrWhiteSpace(apiKey)) _runtimeKeys.TryRemove(p.Name, out _);
        else _runtimeKeys[p.Name] = apiKey.Trim();
        foreach (var key in _clients.Keys.Where(k => k.Provider == p.Name.ToLowerInvariant())) _clients.TryRemove(key, out _);
        return true;
    }

    /// <summary>
    /// What the provider itself lists (GET {BaseUrl}/models), narrowed to the allowlist when there is one. On any
    /// failure the configured list is returned with the reason - a console must still be able to offer something.
    /// </summary>
    public async Task<(IReadOnlyList<string> Models, string? Error)> ListModelsAsync(string provider, CancellationToken ct)
    {
        if (!_providers.TryGetValue(provider, out var p)) return ([], $"provider '{provider}' is not configured");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, p.BaseUrl.TrimEnd('/') + "/models");
            if (KeyFor(p) is { } key) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return (p.Models, $"the provider answered HTTP {(int)response.StatusCode} to GET /models");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var ids = doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray().Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null).OfType<string>().ToList()
                : [];
            var listed = p.AnyModel ? ids : ids.Where(id => p.Models.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
            return (listed.Count > 0 ? listed.Order(StringComparer.OrdinalIgnoreCase).ToList() : p.Models, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (p.Models, $"could not list models: {ex.Message}");
        }
    }

    private string? KeyFor(Provider p) =>
        _runtimeKeys.TryGetValue(p.Name, out var runtime) ? runtime : string.IsNullOrEmpty(p.ConfiguredKey) ? null : p.ConfiguredKey;

    private IChatClient Build(Provider p, string model)
    {
        var openAi = new OpenAIClient(
            new ApiKeyCredential(KeyFor(p) ?? "not-needed"),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(p.BaseUrl),
                NetworkTimeout = TimeSpan.FromSeconds(_timeoutSeconds),
            });

        return new ErrorDetailsChatClient(openAi.GetChatClient(model).AsIChatClient())
            .AsBuilder()
            // gen_ai.* metrics and traces (OpenTelemetry GenAI semantic conventions). Prompts are NOT recorded.
            .UseOpenTelemetry(_loggerFactory, TelemetrySource, t => t.EnableSensitiveData = false)
            .Build();
    }
}
