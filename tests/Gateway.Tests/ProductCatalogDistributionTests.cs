using System.Text;
using AuditionModStudio.Gateway.Services;
using Microsoft.Extensions.Configuration;

namespace Gateway.Tests;

public sealed class ProductCatalogDistributionTests
{
    [Fact]
    public async Task Configured_service_returns_exact_immutable_signed_document_and_stable_etag()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"payload\":\"signed\"}");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:ProductCatalog:SignedDocumentBase64"] = Convert.ToBase64String(bytes),
        }).Build();

        Assert.True(ConfiguredProductCatalogDocumentService.TryCreate(configuration, out var service));
        var first = await service!.GetAsync();
        var second = await service.GetAsync();

        Assert.Equal(ProductCatalogDocumentStatus.Succeeded, first.Status);
        Assert.Equal(bytes, first.Document.ToArray());
        Assert.Equal(first.ETag, second.ETag);
        Assert.StartsWith("\"", first.ETag);
        Assert.EndsWith("\"", first.ETag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-base64")]
    public void Missing_or_invalid_document_fails_closed(string? value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Gateway:ProductCatalog:SignedDocumentBase64"] = value }).Build();
        Assert.False(ConfiguredProductCatalogDocumentService.TryCreate(configuration, out var service));
        Assert.Null(service);
    }
}
