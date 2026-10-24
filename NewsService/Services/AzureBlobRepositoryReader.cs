using Azure;
using Azure.Storage.Blobs;
using NewsCentral.Security;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// Reads content from Azure Blob Storage.
/// Authentication is selected by AzureBlob:AuthMode:
///   "Certificate"  — ClientCertificateCredential, cert loaded from LocalMachine\My by thumbprint.
///   "ClientSecret" — ClientSecretCredential.
/// Set StorageMode=Share in registry for development and on-premises use.
/// </summary>
public sealed class AzureBlobRepositoryReader : IRepositoryReader
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<AzureBlobRepositoryReader> _logger;

    public bool IsAvailable => true;
    public string SyncSource => "Azure";

    public AzureBlobRepositoryReader(
        AzureBlobSection config,
        AzureProxyTransportFactory transportFactory,
        ILogger<AzureBlobRepositoryReader> logger)
    {
        _logger = logger;

        if (string.IsNullOrWhiteSpace(config.AccountName))
            throw new InvalidOperationException("AzureBlob:AccountName must be set.");
        if (string.IsNullOrWhiteSpace(config.ContainerName))
            throw new InvalidOperationException("AzureBlob:ContainerName must be set.");

        var transport   = transportFactory.AzureTransport;
        var credential  = AzureCredentialFactory.Create(config, transport);
        var serviceUri  = new Uri($"https://{config.AccountName}.blob.core.windows.net");

        // Off (transport == null) → construct exactly as before; on → route through the WinHTTP transport.
        var service = transport is null
            ? new BlobServiceClient(serviceUri, credential)
            : new BlobServiceClient(serviceUri, credential, new BlobClientOptions { Transport = transport });
        _container      = service.GetBlobContainerClient(config.ContainerName);

        _logger.LogInformation(
            "Azure Blob reader initialised — account={Account}, container={Container}, authMode={Mode}",
            config.AccountName, config.ContainerName, config.AuthMode);
    }

    public bool TryValidateImagePath(string teamFolder, string imagePath, out string reason)
    {
        var ok = ImagePathValidator.IsBlobNameUnderTeam(imagePath, teamFolder);
        reason = ok ? "ok" : $"blob name '{imagePath}' does not start with '{teamFolder}/'";
        return ok;
    }

    public async Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            var blob     = _container.GetBlobClient(relativePath);
            var response = await blob.DownloadContentAsync(ct);
            return response.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogDebug("Blob not found: {Path}", relativePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read blob: {Path}", relativePath);
            return null;
        }
    }

    public async Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            var blob     = _container.GetBlobClient(relativePath);
            var response = await blob.DownloadContentAsync(ct);
            return response.Value.Content.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogDebug("Blob not found: {Path}", relativePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read blob bytes: {Path}", relativePath);
            return null;
        }
    }

}
