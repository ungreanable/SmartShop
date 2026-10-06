using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartShop.SharedKernel;

namespace SmartShop.Infrastructure.Storage;

public sealed record StoredObject(Stream Content, string ContentType, long Length);

/// <summary>S3-compatible object storage abstraction (SeaweedFS, Garage, AWS S3, Cloudflare R2 ...).</summary>
public interface IObjectStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default);

    Task<StoredObject?> GetAsync(string key, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary><c>S3</c> or <c>FileSystem</c>.</summary>
    public string Provider { get; set; } = "FileSystem";

    public string Bucket { get; set; } = "smartshop";
    public string? ServiceUrl { get; set; }
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public string Region { get; set; } = "us-east-1";
    public string FileSystemRoot { get; set; } = "data/media";
}

internal sealed class S3ObjectStorage(IAmazonS3 s3, StorageOptions options, ILogger<S3ObjectStorage> logger) : IObjectStorage
{
    private volatile bool _bucketChecked;

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        try
        {
            await EnsureBucketAsync(ct);
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = options.Bucket,
                Key = key,
                InputStream = content,
                ContentType = contentType,
                AutoCloseStream = false,
            }, ct);
        }
        // Storage down or still starting (e.g. SeaweedFS after a restart): tell the client to retry instead of a bare 500.
        catch (Exception e) when (e is HttpRequestException or Amazon.Runtime.AmazonServiceException or IOException && !ct.IsCancellationRequested)
        {
            throw new UnavailableException("storage_unavailable", "File storage is not reachable right now. Please try again in a moment.");
        }
    }

    public async Task<StoredObject?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var response = await s3.GetObjectAsync(options.Bucket, key, ct);
            return new StoredObject(response.ResponseStream, response.Headers.ContentType, response.ContentLength);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default) =>
        s3.DeleteObjectAsync(options.Bucket, key, ct);

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketChecked) return;
        try
        {
            await s3.PutBucketAsync(options.Bucket, ct);
        }
        catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }
        catch (AmazonS3Exception e)
        {
            logger.LogWarning(e, "Could not ensure bucket {Bucket} exists", options.Bucket);
        }
        _bucketChecked = true;
    }
}

/// <summary>Local disk storage for development, tests and single-machine deployments without S3.</summary>
internal sealed class FileSystemObjectStorage(StorageOptions options) : IObjectStorage
{
    private string PathFor(string key)
    {
        var root = Path.GetFullPath(options.FileSystemRoot);
        var full = Path.GetFullPath(Path.Combine(root, key));
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new ArgumentException("Invalid storage key.", nameof(key));
        return full;
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
        await File.WriteAllTextAsync(path + ".type", contentType, ct);
    }

    public async Task<StoredObject?> GetAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        var type = File.Exists(path + ".type") ? await File.ReadAllTextAsync(path + ".type", ct) : "application/octet-stream";
        var stream = File.OpenRead(path);
        return new StoredObject(stream, type, stream.Length);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        File.Delete(path);
        File.Delete(path + ".type");
        return Task.CompletedTask;
    }
}

public static class StorageSetup
{
    public static IServiceCollection AddSmartShopStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
        services.AddSingleton(options);

        if (string.Equals(options.Provider, "S3", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
                new BasicAWSCredentials(options.AccessKey, options.SecretKey),
                new AmazonS3Config
                {
                    ServiceURL = options.ServiceUrl,
                    ForcePathStyle = true,
                    AuthenticationRegion = options.Region,
                    RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                    ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
                }));
            services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        }
        else
        {
            services.AddSingleton<IObjectStorage, FileSystemObjectStorage>();
        }
        return services;
    }
}
