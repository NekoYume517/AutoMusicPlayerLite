using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoMusicPlayer;
public sealed partial class MainWindow
{
    private bool automaticUpdates = true, updateBusy;
    private ReleaseVersion? availableUpdate;
    private readonly CancellationTokenSource updateCancellation = new();
    private void AutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        automaticUpdates = AutoUpdateToggle.IsOn;
        if (ready && !selfTest) SaveSettings();
    }
    private async void CheckUpdateClick(object sender, RoutedEventArgs e) => await CheckUpdates(true);
    private async Task CheckUpdates(bool manual)
    {
        if (updateBusy || closing || selfTest) return;
        string stamp = Path.Combine(backend.DataDirectory, "update-last-check.txt");
        if (!manual)
        {
            try { if (File.Exists(stamp) && DateTime.TryParse(await File.ReadAllTextAsync(stamp), out var previous) && DateTime.UtcNow - previous < TimeSpan.FromHours(24)) return; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        updateBusy = true; CheckUpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查稳定版本…";
        try
        {
            availableUpdate = await ReleaseUpdate.Check(updateCancellation.Token, DistributionMode.IsPortable, DistributionMode.IsMsix);
            if (closing) return;
            await File.WriteAllTextAsync(stamp, DateTime.UtcNow.ToString("O"));
            UpdateStatusText.Text = availableUpdate is null ? $"已是最新版本 · {ReleaseUpdate.Current}" : $"发现新版本 {availableUpdate.Version} · 当前 {ReleaseUpdate.Current}";
            UpdateNotesText.Text = availableUpdate?.Notes ?? "";
            InstallUpdateButton.IsEnabled = availableUpdate is not null;
        }
        catch (OperationCanceledException) { if (!closing) UpdateStatusText.Text = "检查超时或已取消，可稍后重试。"; }
        catch (Exception error) { if (!closing) UpdateStatusText.Text = "检查失败：" + error.Message + "。可稍后重试或查看版本发布页。"; }
        finally { updateBusy = false; if (!closing) CheckUpdateButton.IsEnabled = true; }
    }
    private async void InstallUpdateClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (availableUpdate is null || updateBusy) return;
        if (playbackState is "playing" or "preview" or "countdown" or "practice") throw new InvalidOperationException("请先暂停演奏或试听，再安装更新。");
        var release = availableUpdate;
        bool portable = release.IsPortable;
        bool msix = release.IsMsix;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = $"更新到 {release.Version}", Content = portable ? "将下载并校验新版绿色 EXE，关闭播放器后替换当前文件并重新打开。保留当前文件名、曲库、分组、收藏和设置；新窗口启动失败时恢复原文件。" : msix ? "将下载并校验新版 MSIX，关闭播放器后由 Windows 安装更新。曲库、分组、收藏和设置会保留。" : "将下载并校验新版安装包，然后关闭播放器并启动安装。曲库、分组和收藏会保留。", PrimaryButtonText = portable ? "下载并更新" : "下载并安装", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await ShowDialog(dialog) != ContentDialogResult.Primary) return;
        updateBusy = true; CheckUpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = false;
        try
        {
            string installer = await ReleaseUpdate.Download(release, Path.Combine(backend.DataDirectory, "updates"), new Progress<double>(p => UpdateStatusText.Text = $"正在下载 {p:P0}"), updateCancellation.Token);
            if (closing) return;
            var start = portable ? PortableUpdate.StartInfo(installer, DistributionMode.Executable, backend.DataDirectory, Environment.ProcessId) : new ProcessStartInfo(installer) { UseShellExecute = true };
            UpdateStatusText.Text = portable ? "校验通过，更新绿色版…" : "校验通过，启动安装…";
            await backend.Call("stop");
            if (!portable && !msix) start.Arguments = "/SP- /SILENT /NORESTART /DIR=\"" + AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) + "\"";
            var started = Process.Start(start);
            if (!msix && started is null) throw new IOException("系统未能启动更新程序，当前窗口已保留。"); Close();
        }
        finally { updateBusy = false; if (!closing) { CheckUpdateButton.IsEnabled = true; InstallUpdateButton.IsEnabled = true; } }
    });
    private void ReleasePageClick(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(ReleaseUpdate.ReleasesPage) { UseShellExecute = true });
}
