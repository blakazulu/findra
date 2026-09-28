using System.Net.Http;
using System.Security.Cryptography;

namespace Findra;

/// <summary>Why a download did not produce a file that may be run.</summary>
public enum DownloadFailure { None, Host, TooLarge, Short, Digest, Network, Cancelled }

/// <summary>A downloaded installer that checked out, or why there is not one.</summary>
public sealed record DownloadResult(string? Path, DownloadFailure Failure, string? Message)
{
    public static DownloadResult Ok(string path) => new(path, DownloadFailure.None, null);
    public static DownloadResult Failed(DownloadFailure why, string message) => new(null, why, message);
}

/// <summary>
/// Fetches a release's installer and checks it before anything may run it.
///
/// <para>Only over HTTPS, and only from GitHub: <c>github.com</c> answers a release download with a
/// redirect to its file host, so redirects are followed here one at a time and every hop is held
/// to the same rule, rather than handed to a client that would follow them anywhere. The file must
/// then be exactly the declared size and hash to the SHA-256 GitHub published for it. That catches
/// a broken, truncated or swapped download; it cannot catch somebody who controls the account and
/// publishes a bad installer with its own matching digest. Only code signing closes that.</para>
/// </summary>
public static class UpdateDownload
{
    /// <summary>No Findra installer is within a factor of five of this. A larger declared size is
    /// not an installer, and nothing that size is written to the disk to find out.</summary>
    public const long MaxBytes = 512L * 1024 * 1024;

    private const int MaxRedirects = 10;

    public static bool AllowedHost(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.Scheme == Uri.UriSchemeHttps &&
               (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>No cookies, and no redirects followed behind this file's back. No overall timeout
    /// either: an 86 MB download on a slow line outlives the default 100 seconds, and the caller's
    /// token (Cancel, or quitting) is what ends it.</summary>
    public static HttpClient CreateClient() =>
        new(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    public static async Task<DownloadResult> GetAsync(HttpClient client, ReleaseAsset asset, string dir, string version,
                                                      Action<long, long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(asset);

        if (asset.Size <= 0 || asset.Size > MaxBytes)
            return DownloadResult.Failed(DownloadFailure.TooLarge, $"declared as {asset.Size:N0} bytes");
        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out Uri? uri) || !AllowedHost(uri))
            return DownloadResult.Failed(DownloadFailure.Host, "refused " + asset.Url);

        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, asset.Name);
        try
        {
            HttpResponseMessage response;
            for (int hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd($"findra/{version}");
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);
                if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } next) break;

                response.Dispose();
                if (hop >= MaxRedirects)
                    return DownloadResult.Failed(DownloadFailure.Network, "too many redirects");
                uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                if (!AllowedHost(uri))
                    return DownloadResult.Failed(DownloadFailure.Host, "refused a redirect to " + uri.Host);
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();
                await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[81920];
                long got = 0;
                int n;
                while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if (got + n > asset.Size)
                    {
                        file.Close();
                        TryDelete(path);
                        return DownloadResult.Failed(DownloadFailure.Short, "longer than declared");
                    }
                    await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    got += n;
                    progress?.Invoke(got, asset.Size);
                }
            }

            DownloadFailure verdict = Verify(path, asset.Size, asset.Sha256);
            if (verdict == DownloadFailure.None) return DownloadResult.Ok(path);
            TryDelete(path);
            return DownloadResult.Failed(verdict, verdict == DownloadFailure.Digest ? "checksum mismatch" : "incomplete");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(path);
            return DownloadResult.Failed(DownloadFailure.Cancelled, "cancelled");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            TryDelete(path);
            return DownloadResult.Failed(DownloadFailure.Network, ex.Message);
        }
    }

    /// <summary>Exactly <paramref name="size"/> bytes, hashing to <paramref name="sha256"/>. The
    /// digest is compared without regard to case: GitHub's is lower case today, and a file is not
    /// a different file because its hash was written in capitals.</summary>
    public static DownloadFailure Verify(string path, long size, string sha256)
    {
        if (!File.Exists(path)) return DownloadFailure.Short;
        using FileStream s = File.OpenRead(path);
        return Verify(s, size, sha256);
    }

    /// <summary>The same, through a handle the caller already holds - so a caller that keeps the
    /// file locked can check exactly the bytes it is about to run.</summary>
    public static DownloadFailure Verify(FileStream file, long size, string sha256)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length != size) return DownloadFailure.Short;
        file.Position = 0;
        string hex = Convert.ToHexStringLower(SHA256.HashData(file));
        return string.Equals(hex, sha256, StringComparison.OrdinalIgnoreCase) ? DownloadFailure.None : DownloadFailure.Digest;
    }

    /// <summary>Empty the folder. A file still held by a running installer stays until next time.</summary>
    public static void Sweep(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (string f in Directory.EnumerateFiles(dir))
        {
            try { File.Delete(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("update", $"could not delete {System.IO.Path.GetFileName(f)}: {ex.Message}");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("update", "could not delete a refused download: " + ex.Message);
        }
    }
}
