using System.Text;
using System.Text.RegularExpressions;

namespace ImportEvidence.Application;

public enum ScanStatus { Clean, Rejected, Unavailable }
public sealed record ContentScanResult(ScanStatus Status, string? Reason)
{
    public static readonly ContentScanResult Clean = new(ScanStatus.Clean, null);
}
public interface IContentScanner
{
    Task<ContentScanResult> ScanAsync(Stream content, string fileName, CancellationToken ct);
}

/// <summary>
/// Synchronous built-in safety scanner. Deployments can replace this with an AV adapter;
/// scanner failures are fail-closed and never produce a ready upload.
/// </summary>
public sealed partial class BuiltInContentScanner : IContentScanner
{
    private static readonly byte[] Pe = [0x4d, 0x5a];
    private static readonly byte[] Elf = [0x7f, 0x45, 0x4c, 0x46];
    public async Task<ContentScanResult> ScanAsync(Stream content, string fileName, CancellationToken ct)
    {
        try
        {
            content.Position = 0;
            var bytes = new byte[Math.Min(1024 * 1024, content.CanSeek ? content.Length : 1024 * 1024)];
            var read = 0; while (read < bytes.Length) { var n = await content.ReadAsync(bytes.AsMemory(read), ct); if (n == 0) break; read += n; }
            content.Position = 0;
            if (read >= 2 && bytes[..2].AsSpan().SequenceEqual(Pe) || read >= 4 && bytes[..4].AsSpan().SequenceEqual(Elf))
                return new(ScanStatus.Rejected, "Executable content is not accepted.");
            var text = Encoding.UTF8.GetString(bytes, 0, read);
            if (text.Contains("EICAR-STANDARD-ANTIVIRUS-TEST-FILE", StringComparison.OrdinalIgnoreCase))
                return new(ScanStatus.Rejected, "Malware scanning rejected the file.");
            if (SecretPattern().IsMatch(text)) return new(ScanStatus.Rejected, "The file appears to contain a private key or credential.");
            if (Path.GetExtension(fileName).ToLowerInvariant() is ".exe" or ".dll" or ".com" or ".bat" or ".cmd" or ".sh" or ".ps1" or ".app" or ".dylib" or ".so")
                return new(ScanStatus.Rejected, "Executable file types are not accepted.");
            return ContentScanResult.Clean;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        { return new(ScanStatus.Unavailable, "Content scanning is unavailable."); }
    }
    [GeneratedRegex(@"(-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|(?i)(password|client_secret|api[_-]?key)\s*[:=]\s*[^\s]{12,})", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();
}
