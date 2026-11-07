using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SreAgent.Core;

namespace SreAgent.Tools;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentTools(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ToolsOptions>(configuration.GetSection(ToolsOptions.Section));

        // In the cluster: the pod's ServiceAccount token (read-only RBAC). Locally: ~/.kube/config (context kind-sre-ai).
        services.AddSingleton<IKubernetes>(_ => new Kubernetes(KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile()));

        services.AddHttpClient<PrometheusClient>((sp, http) => Configure(http, sp.GetRequiredService<IOptions<ToolsOptions>>().Value.PrometheusUrl));
        services.AddHttpClient<LokiClient>((sp, http) => Configure(http, sp.GetRequiredService<IOptions<ToolsOptions>>().Value.LokiUrl));
        services.AddHttpClient<AlertmanagerClient>((sp, http) => Configure(http, sp.GetRequiredService<IOptions<ToolsOptions>>().Value.AlertmanagerUrl));

        services.AddSingleton<IAgentToolset, AgentTools>();
        services.AddSingleton<IObservationCollector, ObservationCollector>();
        services.AddSingleton<ICompletionCheck, AlertsResolvedCompletionCheck>();
        return services;
    }

    private static void Configure(HttpClient http, string baseUrl)
    {
        http.BaseAddress = new Uri(baseUrl);
        http.Timeout = TimeSpan.FromSeconds(15);
    }
}
