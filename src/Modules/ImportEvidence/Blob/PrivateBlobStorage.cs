using System.Security.Cryptography;
using ProjectIdentity.Application;

namespace ImportEvidence.Blob;

public interface IPrivateBlobStorage
{
    Task<StoredBlob> StoreAsync(Guid projectId, Stream content, long maximumBytes, CancellationToken ct);
    Task<Stream> OpenReadAsync(Guid projectId, string storageKey, CancellationToken ct);
}
public sealed record StoredBlob(string StorageKey, string Sha256, long ByteSize);

/// <summary>Content-addressed files below a private root; the path is never accepted from a client.</summary>
public sealed class PrivateBlobStorage(string root) : IPrivateBlobStorage
{
    public async Task<StoredBlob> StoreAsync(Guid projectId, Stream content, long maximumBytes, CancellationToken ct)
    {
        var projectRoot = Path.Combine(root, projectId.ToString("N"));
        Directory.CreateDirectory(projectRoot);
        var temporary = Path.Combine(projectRoot, ".upload-" + Guid.NewGuid().ToString("N"));
        try
        {
            string hash; long size;
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920]; size = 0;
                while (true)
                {
                    var read = await content.ReadAsync(buffer, ct);
                    if (read == 0) break;
                    size += read;
                    if (size > maximumBytes) throw new ProjectIdentityException("PAYLOAD_TOO_LARGE", $"The file exceeds the {maximumBytes}-byte limit.", 413);
                    sha.AppendData(buffer, 0, read); await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                await file.FlushAsync(ct);
                if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }
            var final = Path.Combine(projectRoot, hash);
            if (!File.Exists(final))
            {
                try { File.Move(temporary, final); }
                catch (IOException) when (File.Exists(final)) { File.Delete(temporary); }
            }
            else File.Delete(temporary);
            return new(hash, hash, size);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task<Stream> OpenReadAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        if (storageKey.Length != 64 || storageKey.Any(c => !Uri.IsHexDigit(c))) throw ProjectIdentityException.NotFound();
        var path = Path.Combine(root, projectId.ToString("N"), storageKey.ToLowerInvariant());
        if (!File.Exists(path)) throw ProjectIdentityException.NotFound();
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Task.FromResult(stream);
    }
}
