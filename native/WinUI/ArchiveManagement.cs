using System.IO.Compression;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoMusicPlayer;
public sealed partial class MainWindow
{
    private async void BatchExportClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var ids = BatchIds(); if (ids.Length == 0) return;
        var path = await SavePicker($"乐谱-{DateTime.Now:yyyyMMdd-HHmmss}", "乐谱 ZIP", ".zip");
        if (path is null) return;
        var result = await backend.Call("export_zip", new { ids, path }, 300);
        StatusText.Text = $"已导出 {result.GetProperty("count")} 首谱面到 ZIP";
    });
    private async void BackupClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var path = await SavePicker($"backup-{DateTime.Now:yyyyMMdd-HHmmss}", "完整备份 ZIP", ".zip");
        if (path is null) return;
        SaveSettings(); await CommitSpeed();
        var result = await backend.Call("backup_zip", new { path }, 300);
        StatusText.Text = $"备份已保存 · {result.GetProperty("count")} 首谱面及全部设置";
    });
    private async void RestoreBackupClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var file = await OpenPicker(".zip").PickSingleFileAsync(); if (file is null) return;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "恢复备份", PrimaryButtonText = "恢复", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock { Text = "恢复将替换当前曲库、分组、收藏和备份内的设置。程序会先自动备份当前数据到数据目录的 backups 文件夹。\n\n请先停止演奏或试听，再继续。", TextWrapping = TextWrapping.Wrap, MaxWidth = 440 } };
        if (await ShowDialog(dialog) != ContentDialogResult.Primary) return;
        seekTimer.Stop(); speedTimer.Stop(); await backend.Call("stop");
        SaveSettings(); await CommitSpeed();
        var result = await backend.Call("restore_backup", new { path = file.Path, confirmed = true }, 300);
        LoadSettings(); AutoUpdateToggle.IsOn = automaticUpdates;
        updating = true;
        ThemeBox.SelectedIndex = savedTheme; SortBox.SelectedIndex = savedSort;
        AdminDefaultToggle.IsOn = preferAdministrator; AdminReminderToggle.IsOn = !suppressAdminReminder;
        updating = false; ApplyTheme();
        var hello = await backend.Call("hello", new { consent = acceptedRisk });
        ApplySpeed(hello.GetProperty("speed").GetDouble());
        profiles = JsonSerializer.Deserialize<List<ProfileItem>>(hello.GetProperty("profiles"))!;
        ProfileBox.ItemsSource = profiles; ProfileBox.SelectedIndex = 0;
        selected = null; score = default; ++loadSequence;
        await RefreshLibrary();
        StatusText.Text = $"已恢复 {result.GetProperty("count")} 首曲目 · 原数据备份：{result.GetProperty("safety_backup")}";
    });
    private async Task CheckArchives(List<string> checks, int id)
    {
        string path = Path.Combine(backend.DataDirectory, "self-test-scores.zip");
        await backend.Call("export_zip", new { ids = new[] { id }, path });
        using (var zip = ZipFile.OpenRead(path))
            if (zip.Entries.Count != 1 || !zip.Entries[0].FullName.StartsWith("scores/") || !zip.Entries[0].FullName.EndsWith(".json")) throw new Exception("Batch export included settings");
        checks.Add("selected score ZIP export contains only score JSON");
        SaveSettings();
        path = Path.Combine(backend.DataDirectory, "self-test-backup.zip");
        await backend.Call("backup_zip", new { path });
        using (var zip = ZipFile.OpenRead(path))
            if (zip.GetEntry("manifest.json") is null || zip.GetEntry("settings/config.yaml") is null || zip.GetEntry("settings/ui-settings.json") is null) throw new Exception("Backup omitted settings");
        if (BackupButton is null || RestoreBackupButton is null || BatchExportButton is null) throw new Exception("Archive controls absent");
        checks.Add("backup ZIP includes library metadata and persisted player/UI settings");
    }
}
