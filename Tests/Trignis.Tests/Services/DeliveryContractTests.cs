using System.Text.Json;
using Trignis.Services;
using Xunit;

namespace Trignis.Tests.Services;

/// <summary>The receiver-facing parts of an export: delivery id, signature and the metrics text.</summary>
public sealed class DeliveryContractTests
{
    [Fact]
    public void DeliveryId_IsStable_AcrossDeadLetterStorageAndReplay()
    {
        using var source = JsonDocument.Parse("""[ { "$operation": "U", "$version": 7, "Id": 1, "Name": "a" } ]""");
        var exported = JsonSerializer.Serialize(source.RootElement);

        // Dead letters store the serialized payload; replay parses it and exports the element again
        using var replayed = JsonDocument.Parse(exported);

        Assert.Equal(DeadLetterService.PayloadHash(exported), DeadLetterService.PayloadHash(JsonSerializer.Serialize(replayed.RootElement)));
    }

    [Fact]
    public void Signature_MatchesHmacSha256OverTimestampAndBody() =>
        // Reference value from Python: hmac.new(b"secret", b'1700000000.[{"a":1}]', hashlib.sha256).hexdigest()
        Assert.Equal("t=1700000000,v1=516856209dea79008d72e48f00c1c767cd294a440ae742a16140cd7375ced090",
            ExportService.Signature("secret", 1700000000, """[{"a":1}]"""));
}
