using System.Net;
using SreAgent.Core;
using SreAgent.Tools;

namespace SreAgent.Host.Tests;

/// <summary>An HttpClient that answers from a function instead of the network.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(respond(request));

    public static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubHandler(respond)) { BaseAddress = new Uri("http://test") };

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}

public class PrometheusClientTests
{
    [Fact]
    public async Task Parses_a_vector()
    {
        var prom = new PrometheusClient(StubHandler.Client(_ => StubHandler.Json(
            """{"status":"success","data":{"resultType":"vector","result":[{"metric":{"slo":"orders-api-availability"},"value":[1,"0.25"]}]}}""")));

        var (samples, error) = await prom.QueryAsync("x", CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(0.25, Assert.Single(samples).Value);
    }

    [Fact]
    public async Task A_prometheus_error_is_returned_as_data()
    {
        var prom = new PrometheusClient(StubHandler.Client(_ => StubHandler.Json(
            """{"status":"error","errorType":"bad_data","error":"parse error"}""", HttpStatusCode.BadRequest)));

        var (samples, error) = await prom.QueryAsync("x{", CancellationToken.None);

        Assert.Empty(samples);
        Assert.Equal("bad_data: parse error", error);
    }

    [Fact]
    public async Task An_html_error_page_from_a_proxy_is_data_not_an_exception()
    {
        var prom = new PrometheusClient(StubHandler.Client(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>502</html>") }));

        var (_, error) = await prom.QueryAsync("x", CancellationToken.None);

        Assert.Contains("not JSON", error);
    }

    [Fact]
    public async Task A_network_error_is_data_not_an_exception()
    {
        var prom = new PrometheusClient(StubHandler.Client(_ => throw new HttpRequestException("connection refused")));

        var (_, error) = await prom.QueryAsync("x", CancellationToken.None);

        Assert.Contains("unreachable", error);
    }

    [Fact]
    public async Task Our_own_cancellation_is_still_an_exception()
    {
        var prom = new PrometheusClient(StubHandler.Client(_ => throw new OperationCanceledException()));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prom.QueryAsync("x", cts.Token));
    }
}

public class CompletionCheckTests
{
    private const string NoAlerts = "[]";
    private const string Empty = """{"status":"success","data":{"resultType":"vector","result":[]}}""";

    private static TaskSpec Task() => new("id", "goal",
        new TriggerEvent(TriggerKind.Incident, [], "test", DateTimeOffset.UtcNow), "sandbox", new AgentLimits(), "English");

    private static AlertsResolvedCompletionCheck Check(Func<string, HttpResponseMessage> prometheus) =>
        new(new AlertmanagerClient(StubHandler.Client(_ => StubHandler.Json(NoAlerts))),
            new PrometheusClient(StubHandler.Client(r => prometheus(Uri.UnescapeDataString(r.RequestUri!.Query)))));

    [Fact]
    public async Task Steady_state_when_nothing_is_wrong()
    {
        var status = await Check(_ => StubHandler.Json(Empty)).CheckAsync(Task(), 1, CancellationToken.None);
        Assert.True(status.Done);
    }

    [Fact]
    public async Task A_burning_sli_keeps_the_run_open()
    {
        var status = await Check(q => StubHandler.Json(q.Contains("sli_error")
            ? """{"status":"success","data":{"resultType":"vector","result":[{"metric":{"slo":"orders-api"},"value":[1,"0.2"]}]}}"""
            : Empty)).CheckAsync(Task(), 1, CancellationToken.None);

        Assert.False(status.Done);
        Assert.Contains("exceed 1%", status.Reason);
    }

    [Fact]
    public async Task Not_being_able_to_read_restarts_or_readiness_is_not_a_pass()
    {
        // Used to be ignored: the run was declared Resolved without knowing whether pods restart.
        var status = await Check(q => q.Contains("restarts_total")
            ? new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("oops") }
            : StubHandler.Json(Empty)).CheckAsync(Task(), 1, CancellationToken.None);

        Assert.False(status.Done);
        Assert.Contains("cannot read container restarts", status.Reason);
    }

    [Fact]
    public async Task Not_ready_pods_keep_the_run_open()
    {
        var status = await Check(q => StubHandler.Json(q.Contains("kube_pod_status_ready")
            ? """{"status":"success","data":{"resultType":"vector","result":[{"metric":{"pod":"orders-api-1"},"value":[1,"1"]}]}}"""
            : Empty)).CheckAsync(Task(), 1, CancellationToken.None);

        Assert.False(status.Done);
        Assert.Contains("orders-api-1", status.Reason);
    }
}

public class ToolScopeTests
{
    private static readonly ToolsOptions Options = new() { Namespaces = ["sandbox"] };

    [Theory]
    [InlineData("sum by (app) (rate(x{namespace=\"sandbox\"}[5m]))")]
    [InlineData("up")]                                                      // no matcher: allowed (documented limit)
    [InlineData("x{namespace=~\"sandbox|sandbox\"}")]
    [InlineData("x{namespace!=\"kube-system\"}")]                            // only excludes
    public void In_scope_queries_pass(string promql) => Options.EnsurePromqlInScope(promql);

    [Theory]
    [InlineData("x{namespace=\"kube-system\"}")]
    [InlineData("x{job=\"a\", namespace = \"monitoring\"}")]
    [InlineData("x{namespace=~\"sandbox|kube-system\"}")]
    [InlineData("x{namespace=~\".*\"}")]
    public void Foreign_namespaces_are_refused(string promql) =>
        Assert.Throws<ArgumentException>(() => Options.EnsurePromqlInScope(promql));

    [Theory]
    [InlineData("orders-api", true)]
    [InlineData("frontend", true)]
    [InlineData("a\", namespace=~\".*", false)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    public void Label_values_are_plain_names(string value, bool expected) =>
        Assert.Equal(expected, ToolsOptions.IsPlainLabelValue(value));

    [Theory]
    [InlineData("Warning|Error|Critical", true)]
    [InlineData("Error", true)]
    [InlineData("Error|", false)]
    [InlineData(".*", false)]
    [InlineData("Error\"} or {job=~\".+", false)]
    public void Level_regexes_are_only_level_names(string value, bool expected) =>
        Assert.Equal(expected, ToolsOptions.IsLevelRegex(value));
}
