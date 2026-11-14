using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sandbox.OrdersApi;

namespace SreAgent.Host.Tests;

/// <summary>Sandbox service rules that can be tested without a database.</summary>
public class OrderRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_or_blank_items_are_a_client_error(string? item) => Assert.NotNull(OrderRules.Validate(item));

    [Fact]
    public void An_item_over_the_limit_is_a_client_error() =>
        Assert.NotNull(OrderRules.Validate(new string('x', OrderRules.MaxItemLength + 1)));

    [Theory]
    [InlineData("coffee")]
    [InlineData("café with milk")]
    public void Normal_items_pass(string item) => Assert.Null(OrderRules.Validate(item));

    [Fact]
    public void The_size_limit_is_inclusive() => Assert.Null(OrderRules.Validate(new string('x', OrderRules.MaxItemLength)));
}

/// <summary>
/// UseFaults relies on routing having chosen the endpoint already (GetEndpoint() is not null). If that ever changes, the
/// faults would stop being injected SILENTLY - and every chaos experiment would "pass". A real Kestrel on a free port.
/// </summary>
public class FaultInjectionTests
{
    [Fact]
    public async Task Faults_hit_routed_requests_but_not_health_probes_or_unknown_routes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Faults:ErrorRatio"] = "1" });
        builder.AddFaults();
        var app = builder.Build();
        app.UseFaults();
        app.MapGet("/work", () => "ok");
        app.MapGet("/health/live", () => "ok");
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, (await http.GetAsync("/work")).StatusCode);   // injected
            Assert.Equal(System.Net.HttpStatusCode.OK, (await http.GetAsync("/health/live")).StatusCode);              // never on probes
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync("/nope")).StatusCode);                // no endpoint: not a service request
        }
        finally { await app.StopAsync(); }
    }
}
