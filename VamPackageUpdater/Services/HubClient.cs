using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VamPackageUpdater.Services;

/// <summary>
/// Talks to VaM Hub's unofficial JSON API to resolve package names to download
/// URLs and then stream the .var bytes.
///
/// Endpoint shape (reverse-engineered from VamToolbox, which in turn matches
/// what VaM itself sends):
///
///   POST https://hub.virtamate.com/citizenx/api.php
///   User-Agent: UnityPlayer/2018.1.9f1 (UnityWebRequest/1.0, libcurl/7.51.0-DEV)
///   X-Unity-Version: 2018.1.9f1
///   Body: { "source":"VaM", "action":"findPackages", "packages":"A.B.1,C.D.latest" }
///
/// Response:
///   { "packages": { "A.B.1": { "filename":"A.B.1.var", "downloadUrl":"..." }, ... } }
///
/// Hub resolves `.latest` to a concrete filename+version on its side. If it
/// can't find a package (paid-only / removed) it returns an empty or "null"
/// downloadUrl.
/// </summary>
public sealed class HubClient : IDisposable
{
    private const string ApiUrl = "https://hub.virtamate.com/citizenx/api.php";
    private const string UnityUserAgent = "UnityPlayer/2018.1.9f1 (UnityWebRequest/1.0, libcurl/7.51.0-DEV)";
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0 Safari/537.36";

    private readonly HttpClient _apiClient;
    private readonly HttpClient _downloadClient;

    public HubClient()
    {
        _apiClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        _apiClient.DefaultRequestHeaders.UserAgent.ParseAdd(UnityUserAgent);
        _apiClient.DefaultRequestHeaders.Add("X-Unity-Version", "2018.1.9f1");

        var downloadHandler = new HttpClientHandler { UseCookies = false };
        _downloadClient = new HttpClient(downloadHandler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
    }

    public async Task<Dictionary<string, HubPackageInfo>> FindPackagesAsync(
        IEnumerable<string> packageNames,
        CancellationToken ct = default)
    {
        var query = new HubQuery
        {
            Source = "VaM",
            Action = "findPackages",
            Packages = string.Join(',', packageNames.Distinct(StringComparer.OrdinalIgnoreCase))
        };

        using var response = await _apiClient.PostAsJsonAsync(ApiUrl, query, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<HubFindPackagesResult>(cancellationToken: ct);
        return result?.Packages ?? new Dictionary<string, HubPackageInfo>();
    }

    /// <summary>
    /// Stream a .var download to disk. Writes to a temp path then moves into place
    /// on success to avoid leaving half-files behind on cancellation/failure.
    /// </summary>
    public async Task<HubDownloadResult> DownloadAsync(
        string downloadUrl,
        string destinationPath,
        IProgress<long>? bytesDownloadedProgress = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        // VaM Hub uses a consent cookie to confirm the user accepted the download-terms banner.
        request.Headers.Add("Cookie", "vamhubconsent=yes");

        using var response = await _downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            return HubDownloadResult.Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return HubDownloadResult.Fail($"Unexpected content-type '{contentType}' (Hub likely returned an error page).");

        var length = response.Content.Headers.ContentLength;
        if (length is null or <= 0)
            return HubDownloadResult.Fail("Empty or missing Content-Length on response.");

        var tempPath = destinationPath + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using (var netStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await netStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    totalRead += read;
                    bytesDownloadedProgress?.Report(totalRead);
                }
            }

            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(tempPath, destinationPath);
            return HubDownloadResult.Ok(length.Value);
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort */ }
            throw;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort */ }
            return HubDownloadResult.Fail(ex.Message);
        }
    }

    public void Dispose()
    {
        _apiClient.Dispose();
        _downloadClient.Dispose();
    }

    // ------- DTOs -------

    private sealed class HubQuery
    {
        [JsonPropertyName("source")]   public string Source { get; set; } = "";
        [JsonPropertyName("action")]   public string Action { get; set; } = "";
        [JsonPropertyName("packages")] public string Packages { get; set; } = "";
    }

    private sealed class HubFindPackagesResult
    {
        [JsonPropertyName("packages")]
        public Dictionary<string, HubPackageInfo>? Packages { get; set; }
    }
}

public sealed class HubPackageInfo
{
    [JsonPropertyName("filename")]    public string? Filename { get; set; }
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }

    /// <summary>True if Hub returned a usable download URL (not empty, not "null", not the ?file= truncation bug).</summary>
    public bool HasUsableDownloadUrl =>
        !string.IsNullOrEmpty(DownloadUrl) &&
        !string.Equals(DownloadUrl, "null", StringComparison.OrdinalIgnoreCase) &&
        !DownloadUrl.EndsWith("?file=", StringComparison.OrdinalIgnoreCase);
}

public sealed record HubDownloadResult(bool Success, long Bytes, string? Error)
{
    public static HubDownloadResult Ok(long bytes) => new(true, bytes, null);
    public static HubDownloadResult Fail(string error) => new(false, 0, error);
}
