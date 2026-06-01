using System.Security.Cryptography;
using System.Text;
using Dragnet.Models;

namespace Dragnet.Identity;

public sealed record DragnetIdentityDocument
{
    public required string OriginName { get; init; }

    public required string OriginId { get; init; }

    public required string PublicKeyPem { get; init; }

    public required string PrivateKeyPem { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class DragnetIdentityService
{
    private readonly string _identityPath;

    public DragnetIdentityService(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _identityPath = Path.Combine(dataDirectory, "identity.json");
    }

    public DragnetIdentityDocument LoadOrCreate(string originName)
    {
        if (File.Exists(_identityPath))
        {
            var existing = File.ReadAllText(_identityPath);
            var identity = System.Text.Json.JsonSerializer.Deserialize<DragnetIdentityDocument>(existing, DragnetJson.Options);
            if (identity is not null)
            {
                if (string.IsNullOrWhiteSpace(originName) ||
                    string.Equals(identity.OriginName, originName, StringComparison.Ordinal))
                {
                    return identity;
                }

                var renamedIdentity = identity with { OriginName = originName };
                File.WriteAllText(_identityPath, System.Text.Json.JsonSerializer.Serialize(renamedIdentity, DragnetJson.Options));
                return renamedIdentity;
            }
        }

        using var rsa = RSA.Create(3072);
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        var identityDocument = new DragnetIdentityDocument
        {
            OriginName = originName,
            OriginId = CreateOriginId(publicKeyPem),
            PublicKeyPem = publicKeyPem,
            PrivateKeyPem = privateKeyPem
        };

        File.WriteAllText(_identityPath, System.Text.Json.JsonSerializer.Serialize(identityDocument, DragnetJson.Options));
        return identityDocument;
    }

    public string Sign(DragnetIdentityDocument identity, string payload)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(identity.PrivateKeyPem);
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(payload),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(signature);
    }

    public bool Verify(DragnetEventEnvelope envelope)
    {
        if (!string.Equals(envelope.OriginId, CreateOriginId(envelope.OriginPublicKeyPem), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(envelope.OriginPublicKeyPem);
        return rsa.VerifyData(
            Encoding.UTF8.GetBytes(envelope.GetSigningPayload()),
            Convert.FromBase64String(envelope.Signature),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
    }

    public static string CreateOriginId(string publicKeyPem)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(publicKeyPem));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
