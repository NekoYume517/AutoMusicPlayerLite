using AutoMusicPlayer;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

int checks = 0;
var current = new Version(2, 2, 6);
void Assert(bool value) { if (!value) throw new Exception("Update check failed at " + checks); checks++; }
JsonElement Fixture(string version = "2.3.0", bool prerelease = false, string? digest = null, string? url = null, long size = 3) => JsonSerializer.SerializeToElement(new {
    draft = false, prerelease, tag_name = "v" + version, body = "Release notes",
    assets = new[] { new { name = $"AutoMusicPlayerLite-{version}-Setup-x64.exe", size,
        browser_download_url = url ?? $"https://github.com/{ReleaseUpdate.Repository}/releases/download/v{version}/AutoMusicPlayerLite-{version}-Setup-x64.exe",
        digest = digest ?? "sha256:" + Convert.ToHexString(SHA256.HashData(new byte[] {1,2,3})).ToLowerInvariant() } } });
var newer = ReleaseUpdate.Parse(Fixture(), current)!;
Assert(newer.Version == new Version(2,3,0) && newer.Notes == "Release notes");
Assert(ReleaseUpdate.Parse(Fixture("2.2.0"), current) is null);
Assert(ReleaseUpdate.Parse(Fixture("2.0.0"), current) is null);
Assert(ReleaseUpdate.Parse(Fixture(prerelease:true), current) is null);
foreach (var bad in new[] { Fixture(digest:""), Fixture(digest:"sha256:"+new string('z',64)), Fixture(url:"https://example.com/setup.exe"), Fixture(size:ReleaseUpdate.MaxInstallerBytes+1) }) {
    bool rejected = false; try { ReleaseUpdate.Parse(bad, current); } catch (InvalidDataException) { rejected = true; } Assert(rejected);
}
JsonElement PortableFixture(bool includePortable = true, bool includeSetup = true) {
    var assets = new List<object>();
    foreach (string kind in new[] { "Setup", "Portable" }) {
        if (kind == "Setup" && !includeSetup || kind == "Portable" && !includePortable) continue;
        string filename = $"AutoMusicPlayerLite-2.3.0-{kind}-x64.exe";
        assets.Add(new { name = filename, size = 3, browser_download_url = $"https://github.com/{ReleaseUpdate.Repository}/releases/download/v2.3.0/{filename}", digest = "sha256:" + Convert.ToHexString(SHA256.HashData(new byte[]{1,2,3})).ToLowerInvariant() });
    }
    return JsonSerializer.SerializeToElement(new { draft=false, prerelease=false, tag_name="v2.3.0", body="Notes", assets });
}
var portable = ReleaseUpdate.Parse(PortableFixture(), current, portable:true)!;
Assert(portable.IsPortable && portable.Download.AbsoluteUri.EndsWith("-Portable-x64.exe"));
Assert(!ReleaseUpdate.Parse(PortableFixture(), current)!.IsPortable);
Assert(ReleaseUpdate.Parse(PortableFixture(includeSetup:false), current, portable:true)!.IsPortable);
bool missingPortable = false;
try { ReleaseUpdate.Parse(PortableFixture(includePortable:false), current, portable:true); } catch(InvalidDataException) { missingPortable=true; }
Assert(missingPortable);
JsonElement MsixFixture(bool duplicate = false, string? digest = null, string? url = null) {
    string filename = "AutoMusicPlayerLite-2.3.0-x64.msix";
    var asset = new { name = filename, size = 3, browser_download_url = url ?? $"https://github.com/{ReleaseUpdate.Repository}/releases/download/v2.3.0/{filename}", digest = digest ?? "sha256:" + Convert.ToHexString(SHA256.HashData(new byte[]{1,2,3})).ToLowerInvariant() };
    return JsonSerializer.SerializeToElement(new { draft=false, prerelease=false, tag_name="v2.3.0", body="Notes", assets = duplicate ? new[]{asset, asset} : new[]{asset} });
}
var msix = ReleaseUpdate.Parse(MsixFixture(), current, msix:true)!;
Assert(msix.IsMsix && !msix.IsPortable && msix.Download.AbsoluteUri.EndsWith("-x64.msix"));
foreach (var invalid in new[]{PortableFixture(), MsixFixture(duplicate:true), MsixFixture(digest:""), MsixFixture(url:"https://example.com/app.msix")}) {
    bool rejected=false; try { ReleaseUpdate.Parse(invalid, current, msix:true); } catch(InvalidDataException) { rejected=true; } Assert(rejected);
}
string directory = Path.Combine(Path.GetTempPath(), "Amp-update-test-" + Guid.NewGuid().ToString("N"));
try {
    using var good = new HttpClient(new PayloadHandler([1,2,3]));
    string path = await ReleaseUpdate.Download(newer, directory, null, CancellationToken.None, good);
    Assert(File.ReadAllBytes(path).SequenceEqual(new byte[]{1,2,3}));
    string green = await ReleaseUpdate.Download(portable, directory, null, CancellationToken.None, good);
    Assert(Path.GetFileName(green) == "AutoMusicPlayerLite-2.3.0-Portable-x64.exe" && File.ReadAllBytes(green).SequenceEqual(new byte[]{1,2,3}));
    string packaged = await ReleaseUpdate.Download(msix, directory, null, CancellationToken.None, good);
    Assert(Path.GetFileName(packaged) == "AutoMusicPlayerLite-2.3.0-x64.msix" && File.ReadAllBytes(packaged).SequenceEqual(new byte[]{1,2,3}));
    foreach (var bytes in new byte[][] { [9,9,9], [1,2], [1,2,3,4] }) {
        using var bad = new HttpClient(new PayloadHandler(bytes));
        bool rejected = false; try { await ReleaseUpdate.Download(newer, directory, null, CancellationToken.None, bad); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected && Directory.GetFiles(directory, "*.partial").Length == 0 && File.ReadAllBytes(path).SequenceEqual(new byte[]{1,2,3}));
    }
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    bool cancelled = false; try { await ReleaseUpdate.Download(newer, directory, null, cancellation.Token, good); } catch (OperationCanceledException) { cancelled = true; }
    Assert(cancelled && Directory.GetFiles(directory,"*.partial").Length == 0);
} finally { if (Directory.Exists(directory)) Directory.Delete(directory,true); }
var assemblyVersion = typeof(ReleaseUpdate).Assembly.GetName().Version!;
Assert(ReleaseUpdate.Current == new Version(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build));
Assert(ReleaseUpdate.Parse(Fixture("2.2.6"), current) is null);
Console.WriteLine(JsonSerializer.Serialize(new {passed=true,count=checks}));
class PayloadHandler(byte[] bytes) : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
