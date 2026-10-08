using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.Text.Json;

namespace AutoMusicPlayer;
public sealed partial class MainWindow
{
    private bool preferAdministrator, suppressAdminReminder, restarting, isAdministrator;
    private enum AdminChoice { Continue, Restart, RestartAndDefault, DontRemind }
    private void AdminDefaultToggled(object sender, RoutedEventArgs e)
    {
        preferAdministrator = AdminDefaultToggle.IsOn;
        if (ready && !selfTest) SaveSettings();
    }
    private void AdminReminderToggled(object sender, RoutedEventArgs e)
    {
        suppressAdminReminder = !AdminReminderToggle.IsOn;
        if (ready && !selfTest) SaveSettings();
    }
    private async Task<AdminChoice> AdminReminder()
    {
        var choice = AdminChoice.Continue;
        var content = new StackPanel { Width = 460, Spacing = 10 };
        content.Children.Add(new TextBlock { Text = "游戏以管理员身份运行时，播放器也需要相同权限。你可以保留当前普通身份，或授权重启为管理员。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "当前未以管理员身份运行", Content = content };
        foreach (var (text, value) in new[] { ("关闭弹窗，保持当前普通身份运行", AdminChoice.Continue), ("以管理员身份重启", AdminChoice.Restart), ("以管理员身份重启并设置为默认启动方式", AdminChoice.RestartAndDefault), ("不再提醒", AdminChoice.DontRemind) })
        {
            var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };
            if (value == AdminChoice.Restart) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            button.Click += (_, _) => { choice = value; dialog.Hide(); };
            content.Children.Add(button);
        }
        await ShowTestableDialog(dialog, content, "administrator-reminder-preview.png");
        return choice;
    }
    private void ApplyDontRemind()
    {
        suppressAdminReminder = true; AdminReminderToggle.IsOn = false; SaveSettings();
    }
    private async Task CheckAdministratorStartup()
    {
        if (ElevationHandoff.ShouldAutoElevate(isAdministrator, preferAdministrator, selfTest))
            if (await RestartAdministrator(false)) return;
        if (!ElevationHandoff.ShouldRemind(isAdministrator, suppressAdminReminder, selfTest) || closing) return;
        var choice = await AdminReminder();
        if (choice == AdminChoice.DontRemind) ApplyDontRemind();
        else if (choice is AdminChoice.Restart or AdminChoice.RestartAndDefault) await RestartAdministrator(choice == AdminChoice.RestartAndDefault);
    }
    private async void AdminClick(object sender, RoutedEventArgs e) => await Run(async () => await RestartAdministrator(false));
    private async Task<bool> RestartAdministrator(bool setDefault)
    {
        if (restarting || closing) return false;
        if (isAdministrator)
        {
            if (setDefault) { AdminDefaultToggle.IsOn = true; SaveSettings(); }
            StatusText.Text = "当前已经以管理员身份运行。"; return false;
        }
        restarting = true; Nav.IsEnabled = false;
        try
        {
            await backend.Call("stop");
            await backend.Call("hello", new { consent = false });
            StatusText.Text = "等待授权和管理员窗口就绪…";
            string token = Guid.NewGuid().ToString("N");
            var start = ElevationHandoff.StartInfo(Environment.ProcessPath!, backend.DataDirectory, token, setDefault, Environment.GetCommandLineArgs().Contains("--test-driver"));
            var app = (App)Application.Current;
            var result = await ElevationHandoff.Start(start, backend.DataDirectory, token, app.Instance.Release, app.Instance.Acquire);
            StatusText.Text = result.Message;
            if (result.Started) { Close(); return true; }
            await backend.Call("hello", new { consent = acceptedRisk });
            return false;
        }
        finally
        {
            if (!closing) { try { await backend.Call("hello", new { consent = acceptedRisk }); } catch { } Nav.IsEnabled = true; }
            restarting = false;
        }
    }
    private async Task CheckAdministratorControls(List<string> checks)
    {
        var originalDefault = preferAdministrator; var originalReminder = suppressAdminReminder;
        AdminDefaultToggle.IsOn = true; SaveSettings();
        using (var saved = JsonDocument.Parse(File.ReadAllText(settingsFile)))
            if (!saved.RootElement.GetProperty("preferAdministrator").GetBoolean()) throw new Exception("Administrator default did not persist");
        ApplyDontRemind();
        using (var saved = JsonDocument.Parse(File.ReadAllText(settingsFile)))
            if (!saved.RootElement.GetProperty("suppressAdminReminder").GetBoolean()) throw new Exception("Reminder suppression did not persist");
        AdminDefaultToggle.IsOn = originalDefault; AdminReminderToggle.IsOn = !originalReminder; SaveSettings();
        checks.Add("administrator default and reminder suppression persist independently");
        if (await AdminReminder() != AdminChoice.Continue) throw new Exception("Administrator dialog cancel changed privileges");
        checks.Add("administrator startup reminder renders four buttons and closes in normal mode");
        string token = Guid.NewGuid().ToString("N");
        var app = (App)Application.Current;
        var probe = ElevationHandoff.StartInfo(Environment.ProcessPath!, backend.DataDirectory, token, false, true, probe: true);
        var result = await ElevationHandoff.Start(probe, backend.DataDirectory, token, app.Instance.Release, app.Instance.Acquire, requireAdmin: false);
        if (!result.Started) throw new Exception("Real restart handoff failed: " + result.Message);
        var watch = Stopwatch.StartNew();
        while (!app.Instance.Acquire() && watch.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(120);
        if (!app.Instance.Owned) throw new Exception("Instance ownership not restored after recording-driver probe");
        File.Delete(ElevationHandoff.TicketPath(backend.DataDirectory, token));
        checks.Add("real WinUI child restart preserves data directory, waits for ready and reuses existing mutex");
    }
}
