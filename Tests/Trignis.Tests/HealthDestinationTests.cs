using System.Collections.Generic;
using Trignis.Services;
using Xunit;

namespace Trignis.Tests;

public class HealthDestinationTests
{
    private static readonly Dictionary<string, ConnectionHealth> Healthy = new()
    {
        ["sql:prod/Main"] = new ConnectionHealth { IsHealthy = false },
        ["rabbitmq:prod/orders"] = new ConnectionHealth { IsHealthy = true },
    };

    [Fact]
    public void Destinations_AreOk_WhenQueuesAreUpAndCircuitsClosed() =>
        Assert.Equal("ok", HealthCheckService.CheckDestinations(Healthy, []).Status);

    [Fact]
    public void Destinations_AreDegraded_WhenAQueueIsDown()
    {
        var connections = new Dictionary<string, ConnectionHealth>(Healthy)
        {
            ["kafka:prod/events"] = new ConnectionHealth { IsHealthy = false },
        };

        var result = HealthCheckService.CheckDestinations(connections, []);

        Assert.Equal("degraded", result.Status);
        Assert.Equal(["kafka:prod/events"], result.Unhealthy);
    }

    [Fact]
    public void Destinations_AreDegraded_WhenAnHttpEndpointIsFailing() =>
        Assert.Equal(["http:prod/orders"], HealthCheckService.CheckDestinations(Healthy, [], ["http:prod/orders"]).Unhealthy);

    [Fact]
    public void Destinations_AreDegraded_WhenACircuitIsOpen() =>
        Assert.Equal("degraded", HealthCheckService.CheckDestinations(Healthy, ["prod.json/orders"]).Status);
}
