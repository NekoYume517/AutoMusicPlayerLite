using AutoMusicPlayer;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

int checks = 0;
void Assert(bool value) { if (!value) throw new Exception("Update check failed at " + checks); checks++; }
JsonElement Fixture(string version = "2.2.0", bool prerelease = false, string? digest = null, string? url = null, long size = 3) => JsonSerializer.SerializeToElement(new {
    draft = false, prerelease, tag_name = "v" + version, body = "Release notes",
    assets = new[] { new { name = $"AutoMusicPlayerLite-{version}-Setup-x64.exe", size,
        browser_download_url = url ?? $"https://github.com/{ReleaseUpdate.Repository}/releases/download/v{version}/AutoMusicPlayerLite-{version}-Setup-x64.exe",
        digest = digest ?? "sha256:" + Convert.ToHexString(SHA256.HashData(new byte[] {1,2,3})).ToLowerInvariant() } } });
var newer = ReleaseUpdate.Parse(Fixture(), ReleaseUpdate.Current)!;
Assert(newer.Version == new Version(2,2,0) && newer.Notes == "Release notes");
Assert(ReleaseUpdate.Parse(Fixture("2.1.0"), ReleaseUpdate.Current) is null);
Assert(ReleaseUpdate.Parse(Fixture("2.0.0"), ReleaseUpdate.Current) is null);
Assert(ReleaseUpdate.Parse(Fixture(prerelease:true), ReleaseUpdate.Current) is null);
foreach (var bad in new[] { Fixture(digest:""), Fixture(digest:"sha256:"+new string('z',64)), Fixture(url:"https://example.com/setup.exe"), Fixture(size:ReleaseUpdate.MaxInstallerBytes+1) }) {
    bool rejected = false; try { ReleaseUpdate.Parse(bad, ReleaseUpdate.Current); } catch (InvalidDataException) { rejected = true; } Assert(rejected);
}
string directory = Path.Combine(Path.GetTempPath(), "Amp-update-test-" + Guid.NewGuid().ToString("N"));
try {
    using var good = new HttpClient(new PayloadHandler([1,2,3]));
    string path = await ReleaseUpdate.Download(newer, directory, null, CancellationToken.None, good);
    Assert(File.ReadAllBytes(path).SequenceEqual(new byte[]{1,2,3}));
    foreach (var bytes in new byte[][] { [9,9,9], [1,2], [1,2,3,4] }) {
        using var bad = new HttpClient(new PayloadHandler(bytes));
        bool rejected = false; try { await ReleaseUpdate.Download(newer, directory, null, CancellationToken.None, bad); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected && Directory.GetFiles(directory, "*.partial").Length == 0 && File.ReadAllBytes(path).SequenceEqual(new byte[]{1,2,3}));
    }
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    bool cancelled = false; try { await ReleaseUpdate.Download(newer, directory, null, cancellation.Token, good); } catch (OperationCanceledException) { cancelled = true; }
    Assert(cancelled && Directory.GetFiles(directory,"*.partial").Length == 0);
} finally { if (Directory.Exists(directory)) Directory.Delete(directory,true); }
Console.WriteLine(JsonSerializer.Serialize(new {passed=true,count=checks}));
class PayloadHandler(byte[] bytes) : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
