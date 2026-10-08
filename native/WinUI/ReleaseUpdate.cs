using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AutoMusicPlayer;
public sealed record ReleaseVersion(Version Version, string Tag, string Notes, Uri Download, string Sha256, long Size);
public static class ReleaseUpdate
{
    public const string Repository = "NekoYume517/AutoMusicPlayerLite";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    public const long MaxInstallerBytes = 300L * 1024 * 1024;
    public static readonly Version Current = new(2, 1, 0);
    private static readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(8) };
    static ReleaseUpdate()
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AutoMusicPlayerLite/2.1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }
    public static ReleaseVersion? Parse(JsonElement release, Version current)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version <= current) return null;
        string expectedName = $"AutoMusicPlayerLite-{version}-Setup-x64.exe";
        var matches = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == expectedName).ToList();
        if (matches.Count != 1) throw new InvalidDataException("新版本没有唯一的 x64 安装包。");
        var asset = matches[0];
        long size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaxInstallerBytes) throw new InvalidDataException("安装包大小超出允许范围。");
        string digest = asset.TryGetProperty("digest", out var digestValue) ? digestValue.GetString() ?? "" : "";
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
            throw new InvalidDataException("新版本未提供有效的 SHA-256，已停止自动下载。可查看版本发布页。");
        var download = new Uri(asset.GetProperty("browser_download_url").GetString() ?? "");
        string expected = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{expectedName}";
        if (!string.Equals(download.AbsoluteUri, expected, StringComparison.Ordinal) || !string.IsNullOrEmpty(download.UserInfo))
            throw new InvalidDataException("安装包地址不属于本项目的 Release。");
        return new(version, tag, release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", download, digest[7..].ToLowerInvariant(), size);
    }
    public static async Task<ReleaseVersion?> Check(CancellationToken cancellation)
    {
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
        return Parse(json.RootElement, Current);
    }
    public static async Task<string> Download(ReleaseVersion release, string directory, IProgress<double>? progress, CancellationToken cancellation, HttpClient? transport = null)
    {
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, $"AutoMusicPlayerLite-{release.Version}-Setup-x64.exe");
        string partial = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var response = await (transport ?? client).GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != release.Size) throw new InvalidDataException("安装包长度与发布记录不一致。");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = 0; byte[] buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellation)) != 0)
                {
                    total += read;
                    if (total > release.Size || total > MaxInstallerBytes) throw new InvalidDataException("安装包超过发布记录大小。");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    progress?.Report((double)total / release.Size);
                }
                string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (total != release.Size || !string.Equals(actualHash, release.Sha256, StringComparison.Ordinal)) throw new InvalidDataException("安装包 SHA-256 校验失败，未启动安装。");
            }
            File.Move(partial, target, true); return target;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
