using System.Collections.Immutable;
using System.Security.Cryptography;

namespace AuditionModStudio.Gateway.Services;

public enum ProductCatalogDocumentStatus { Succeeded, Unavailable }

public sealed record ProductCatalogDocumentResult(ProductCatalogDocumentStatus Status,
    string DiagnosticCode, ImmutableArray<byte> Document, string? ETag);

public interface IProductCatalogDocumentService
{
    Task<ProductCatalogDocumentResult> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class ConfiguredProductCatalogDocumentService : IProductCatalogDocumentService
{
    public const int MaximumDocumentBytes = 4 * 1024 * 1024;
    private readonly ImmutableArray<byte> _document;
    private readonly string _etag;

    private ConfiguredProductCatalogDocumentService(byte[] document)
    {
        _document = ImmutableArray.Create(document);
        _etag = '"' + Convert.ToHexString(SHA256.HashData(document)) + '"';
    }

    public static bool TryCreate(IConfiguration configuration,
        out ConfiguredProductCatalogDocumentService? service)
    {
        service = null;
        var base64 = configuration["Gateway:ProductCatalog:SignedDocumentBase64"];
        if (string.IsNullOrWhiteSpace(base64) || base64.Length > MaximumDocumentBytes * 2) return false;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length is <= 0 or > MaximumDocumentBytes) return false;
            service = new(bytes);
            return true;
        }
        catch (FormatException) { return false; }
    }

    public Task<ProductCatalogDocumentResult> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(cancellationToken.IsCancellationRequested
            ? new ProductCatalogDocumentResult(ProductCatalogDocumentStatus.Unavailable,
                "PRODUCT_CATALOG_CANCELLED", [], null)
            : new ProductCatalogDocumentResult(ProductCatalogDocumentStatus.Succeeded,
                "PRODUCT_CATALOG_DOCUMENT_READY", _document, _etag));
}

public sealed class UnavailableProductCatalogDocumentService : IProductCatalogDocumentService
{
    public Task<ProductCatalogDocumentResult> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProductCatalogDocumentResult(ProductCatalogDocumentStatus.Unavailable,
            cancellationToken.IsCancellationRequested ? "PRODUCT_CATALOG_CANCELLED"
                : "PRODUCT_CATALOG_UNAVAILABLE", [], null));
}
