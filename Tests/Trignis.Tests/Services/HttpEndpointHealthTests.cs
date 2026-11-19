using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Trignis.Models;
using Trignis.Services;
using Trignis.Tests.Stress;
using Xunit;

namespace Trignis.Tests.Services;

public class HttpEndpointHealthTests
{
    private sealed class Clients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Fact]
    public async Task FailedHttpExport_MarksEndpointFailing_UntilAnExportSucceeds()
    {
        await using var sink = new HttpSink();
        var settings = Options.Create(new GlobalSettings { RetryCount = 0, RetryDelaySeconds = 0, ExportToFile = false, ExportToApi = true });
        var exports = new ExportService(
            NullLogger<ExportService>.Instance,
            new Clients(),
            new MessageQueueService(NullLogger<MessageQueueService>.Instance),
            new OAuth2TokenService(NullLogger<OAuth2TokenService>.Instance, new Clients()),
            new RetryPolicies(NullLogger<RetryPolicies>.Instance, settings),
            settings);
        var endpoint = new ApiEndpoint { Key = "orders", Url = sink.Url };
        var environment = new EnvironmentConfig { Name = "prod", ChangeTracking = new() { ApiEndpoints = [endpoint] } };
        var tracking = new TrackingObject { Name = "Items", Database = "Main", TableName = "dbo.Items", StoredProcedureName = "dbo.sync" };
        using var data = JsonDocument.Parse("""[{"id":1,"$version":1}]""");

        sink.Status = 500;
        await exports.ExportAsync(environment, tracking, data.RootElement, CancellationToken.None);
        Assert.Equal(["http:prod/orders"], exports.FailingHttpEndpoints());

        sink.Status = 200;
        await exports.ExportAsync(environment, tracking, data.RootElement, CancellationToken.None);
        Assert.Empty(exports.FailingHttpEndpoints());
    }
}
