using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using WinRT.Interop;
namespace AutoMusicPlayer;
public sealed partial class MainWindow : Window
{
    private readonly BackendClient backend;
    private List<SongItem> songs = [];
    private List<ProfileItem> profiles = [];
    private SongItem? selected;
    private JsonElement score;
    private string playbackState = "idle";
    private bool updating;
    private bool ready;
    private bool closing;
    private bool dialogOpen;
    private int busy;
    private int loadSequence;
    private MiniWindow? mini;
    private readonly bool selfTest;
    private readonly string? selfTestFile;
    private readonly DispatcherTimer seekTimer;
    private readonly DispatcherTimer speedTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private double globalSpeed = 1;
    private bool speedUpdating, speedPending;
    private readonly string settingsFile;
    private int savedTheme;
    private bool acceptedRisk;
    public nint Hwnd => WindowNative.GetWindowHandle(this);
    public MainWindow()
    {
        var args = Environment.GetCommandLineArgs();
        selfTest = args.Contains("--self-test");
        selfTestFile = Arg(args, "--self-test");
        backend = new BackendClient(((App)Application.Current).DataDirectory);
        settingsFile = Path.Combine(backend.DataDirectory, "ui-settings.json");
        InitializeComponent();
        LoadSettings();
        AutoUpdateToggle.IsOn = automaticUpdates;
        AdminDefaultToggle.IsOn = preferAdministrator;
        AdminReminderToggle.IsOn = !suppressAdminReminder;
        SortBox.SelectedIndex = savedSort;
        Title = "自动演奏器 Lite";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 880));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        ThemeBox.SelectedIndex = savedTheme;
        ApplyTheme();
        Nav.SelectedItem = Nav.MenuItems[0];
        backend.Event += (name, data) => DispatcherQueue.TryEnqueue(() => OnBackendEvent(name, data));
        Root.Loaded += RootLoaded;
        Root.SizeChanged += (_, _) =>
        {
            bool compact = Root.ActualWidth < 1050;
            Grid.SetColumn(RhythmSettingsCard, compact ? 0 : 1);
            Grid.SetRow(RhythmSettingsCard, compact ? 1 : 0);
            SettingsGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        };
        AppWindow.Closing += WindowClosing;
        seekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        seekTimer.Tick += async (_, _) => { seekTimer.Stop(); await Run(async () => await backend.Call("seek", new { position = (int)SeekSlider.Value })); };
        speedTimer.Tick += async (_, _) => await Run(CommitSpeed);
    }
    private static string? Arg(string[] args, string flag)
    {
        int index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--") ? args[index + 1] : null;
    }
    private static bool argsRestartProbe() => Environment.GetCommandLineArgs().Contains("--restart-probe") && Environment.GetCommandLineArgs().Contains("--test-driver");
    private void LoadSettings()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsFile));
            savedTheme = doc.RootElement.GetProperty("theme").GetInt32();
            acceptedRisk = doc.RootElement.GetProperty("acceptedRisk").GetBoolean();
            if (doc.RootElement.TryGetProperty("autoUpdate", out var update)) automaticUpdates = update.GetBoolean();
            if (doc.RootElement.TryGetProperty("preferAdministrator", out var admin)) preferAdministrator = admin.GetBoolean();
            if (doc.RootElement.TryGetProperty("suppressAdminReminder", out var reminder)) suppressAdminReminder = reminder.GetBoolean();
            if (doc.RootElement.TryGetProperty("librarySort", out var sort)) savedSort = Math.Clamp(sort.GetInt32(), 0, 5);
        }
        catch (Exception) { }
    }
    private void SaveSettings()
    {
        File.WriteAllText(settingsFile, JsonSerializer.Serialize(new { theme = savedTheme, acceptedRisk, librarySort = savedSort, preferAdministrator, suppressAdminReminder, autoUpdate = automaticUpdates }));
    }
    private async void RootLoaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= RootLoaded;
        try
        {
            backend.Start(selfTest || Environment.GetCommandLineArgs().Contains("--test-driver"));
            var hello = await backend.Call("hello", new { consent = acceptedRisk || selfTest });
            ApplySpeed(hello.GetProperty("speed").GetDouble());
            profiles = JsonSerializer.Deserialize<List<ProfileItem>>(hello.GetProperty("profiles"))!;
            ProfileBox.ItemsSource = profiles;
            var active = hello.GetProperty("active_profile").GetString();
            ProfileBox.SelectedItem = profiles.FirstOrDefault(p => p.Id == active) ?? profiles[0];
            isAdministrator = ElevationHandoff.IsAdministrator();
            PermissionText.Text = isAdministrator ? "当前以管理员身份运行。" : "当前以普通用户权限运行。";
            DataPathText.Text = backend.DataDirectory;
            bool restartProbe = argsRestartProbe();
            if (!acceptedRisk && !selfTest && !restartProbe)
            {
                var risk = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "首次使用提示", PrimaryButtonText = "我已知晓，继续使用", CloseButtonText = "退出", DefaultButton = ContentDialogButton.Close,
                    Content = new TextBlock { Text = "本工具免费开源，仅供学习交流与个人娱乐。自动键鼠输入可能影响游戏账号，请阅读并遵守游戏规则，使用产生的风险由自己承担。\n\n先使用钢琴试听确认旋律；正式演奏时，请在 3 秒倒计时内切到游戏窗口。F8 可随时暂停并释放按键。", TextWrapping = TextWrapping.Wrap, MaxWidth = 440 } };
                if (await ShowDialog(risk) != ContentDialogResult.Primary) { Close(); return; }
                acceptedRisk = true; SaveSettings();
                await backend.Call("hello", new { consent = true });
            }
            ready = true;
            await RefreshLibrary();
            StatusText.Text = "本地引擎已就绪";
            if (closing) return;
            string? restartToken = Arg(Environment.GetCommandLineArgs(), "--restart-token");
            if (restartToken is not null)
            {
                if (!isAdministrator && !restartProbe) throw new InvalidOperationException("新窗口没有获得管理员权限。");
                if (isAdministrator && Environment.GetCommandLineArgs().Contains("--set-default-admin"))
                { AdminDefaultToggle.IsOn = true; SaveSettings(); }
                ElevationHandoff.Report(backend.DataDirectory, restartToken, "ready");
                if (restartProbe) { await Task.Delay(800); Close(); }
                else if (automaticUpdates) _ = CheckUpdates(false);
                return;
            }
            if (selfTest) await SelfTest();
            else
            {
                await CheckAdministratorStartup();
                if (!closing && automaticUpdates) _ = CheckUpdates(false);
            }
        }
        catch (Exception exc)
        {
            string? failedToken = Arg(Environment.GetCommandLineArgs(), "--restart-token");
            if (failedToken is not null && !closing)
            {
                ((App)Application.Current).Instance.Release();
                ElevationHandoff.Report(backend.DataDirectory, failedToken, "failed", exc.Message);
                Close(); return;
            }
            if (selfTest)
            {
                File.WriteAllText(selfTestFile!, JsonSerializer.Serialize(new { passed = false, error = exc.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
                Close();
            }
            else await ShowError(exc.Message);
        }
    }
    private async Task Run(Func<Task> action)
    {
        if (!ready || closing) return;
        busy++; BusyRing.IsActive = true;
        try { await action(); }
        catch (Exception e) { await ShowError(e.Message); }
        finally { busy--; BusyRing.IsActive = busy > 0; }
    }
    private async Task ShowError(string text)
    {
        StatusText.Text = text;
        if (mini is not null) { mini.ShowMessage(text); return; }
        if (closing || selfTest || dialogOpen) return;
        dialogOpen = true;
        try
        {
            await ShowDialog(new ContentDialog { XamlRoot = Root.XamlRoot, Title = "未能完成操作", Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 }, CloseButtonText = "知道了" });
        }
        finally { dialogOpen = false; }
    }
    private void OnBackendEvent(string name, JsonElement data)
    {
        if (closing) return;
        if (name == "error") { _ = ShowError(data.GetString()!); return; }
        if (name == "notice") { StatusText.Text = data.GetString(); return; }
        if (name == "hotkey_play") { _ = StartPlayback(); return; }
        if (name != "status") return;
        playbackState = data.GetProperty("state").GetString()!;
        if (!speedPending && data.TryGetProperty("speed", out var speed)) ApplySpeed(speed.GetDouble());
        var text = data.GetProperty("message").GetString()!;
        PlaybackMessage.Text = text;
        StatusText.Text = text;
        int position = data.GetProperty("position").GetInt32();
        int total = data.GetProperty("total").GetInt32();
        ProgressText.Text = $"{position} / {total}";
        updating = true;
        SeekSlider.Maximum = Math.Max(total, 1);
        SeekSlider.Value = position;
        updating = false;
        bool active = playbackState is "playing" or "countdown" or "practice" or "preview";
        PlayerSong.IsEnabled = ProfileBox.IsEnabled = ModeBox.IsEnabled = ScenarioBox.IsEnabled = BpmBox.IsEnabled =
            TransposeBox.IsEnabled = HumanizeSwitch.IsEnabled = LatencyBox.IsEnabled = StartBox.IsEnabled = EndBox.IsEnabled = !active;
        PlayButton.IsEnabled = selected is not null && !active;
        ResetButton.IsEnabled = selected is not null;
        PauseButton.IsEnabled = active;
        PreviewButton.IsEnabled = selected is not null && playbackState is not ("playing" or "countdown" or "practice");
        PreviewButton.Content = playbackState == "preview" ? "停止试听" : "钢琴试听";
        SeekSlider.IsEnabled = !active && total > 0;
        PlayButton.Content = playbackState == "paused" && position > 0 ? "继续演奏 · F6" : "开始演奏 · F6";
        TargetText.Text = data.GetProperty("target").GetString() is { Length: > 0 } title ? $"目标窗口：{title}" : "尚未锁定游戏窗口";
        PracticePanel.Visibility = playbackState == "practice" || data.GetProperty("hits").GetInt32() > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScoreHint.Text = "乐谱：" + data.GetProperty("score_hint").GetString();
        InputHint.Text = "输入：" + data.GetProperty("input_hint").GetString();
        PracticeStats.Text = $"命中 {data.GetProperty("hits")}  ·  错误 {data.GetProperty("misses")}";
        mini?.Update(text, position, total, playbackState,
            data.TryGetProperty("elapsed_seconds", out var elapsed) ? elapsed.GetDouble() : 0,
            data.TryGetProperty("total_seconds", out var duration) ? duration.GetDouble() : 0);
    }
    private void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string page = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "library";
        LibraryPage.Visibility = page == "library" ? Visibility.Visible : Visibility.Collapsed;
        PlayerPage.Visibility = page == "player" ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = page == "logs" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch { "player" => "演奏控制", "logs" => "演奏记录", "settings" => "设置", _ => "乐谱库" };
        PageSubtitle.Text = page switch { "player" => "调整节奏与片段；在乐谱库或小窗中开始演奏。", "logs" => "查看演奏进度、输入事件与节奏偏差。", "settings" => "按你的习惯设置外观、权限与数据。", _ => "" };
        LibraryCount.Visibility = page == "library" ? Visibility.Visible : Visibility.Collapsed;
        PageSubtitle.Visibility = page == "library" ? Visibility.Collapsed : Visibility.Visible;
        RefreshLogsButton.Visibility = page == "logs" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "logs" && ready) _ = Run(RefreshLogs);
    }
    private async Task RefreshLibrary()
    {
        int selectedId = selected?.Id ?? 0;
        var result = await backend.Call("list");
        songs = JsonSerializer.Deserialize<List<SongItem>>(result)!;
        libraryGroups = JsonSerializer.Deserialize<List<LibraryGroup>>(await backend.Call("groups"))!;
        updating = true;
        PlayerSong.ItemsSource = songs;
        RefreshTabs();
        FilterSongs();
        selected = songs.FirstOrDefault(s => s.Id == selectedId);
        if (!multiSelect) SongsList.SelectedItem = selected;
        PlayerSong.SelectedItem = selected;
        updating = false;
        SelectedTitle.Text = selected?.Name ?? "尚未选择";
        SelectedMeta.Text = selected?.Subtitle ?? "从左侧选择一首曲目。";
        GoButton.IsEnabled = EditButton.IsEnabled = ExportButton.IsEnabled = DeleteButton.IsEnabled = selected is not null;
        PlayButton.IsEnabled = PreviewButton.IsEnabled = ResetButton.IsEnabled = selected is not null;
        if (selected is null) { ScoreDescription.Text = "先在乐谱库选择一首曲目。"; score = default; }
        mini?.UpdateSongs(songs, selected);
        UpdateBatchControls();
    }
    private void FilterSongs()
    {
        bool wasUpdating = updating; updating = true;
        var query = songs.Where(s => s.Matches(SearchBox.Text.Trim()) && (currentGroup == 0 || (currentGroup == -1 ? s.Favorite : s.GroupIds.Contains(currentGroup))));
        Func<SongItem, string> key = SortBox.SelectedIndex switch { 2 or 3 => s => s.UpdatedAt, 4 or 5 => s => s.NameSortKey, _ => s => s.CreatedAt };
        bool descending = SortBox.SelectedIndex % 2 == 1;
        var filtered = (descending ? query.OrderByDescending(key, StringComparer.Ordinal).ThenByDescending(s => s.Id) : query.OrderBy(key, StringComparer.Ordinal).ThenBy(s => s.Id)).ToList();
        visibleSongs = filtered; SongsList.ItemsSource = filtered;
        if (!multiSelect && selected is not null && filtered.Contains(selected)) SongsList.SelectedItem = selected;
        EmptyLibrary.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyLibraryText.Text = filtered.Count == 0 ? "当前分组没有匹配曲目，可导入乐谱或加入曲目" : "";
        LibraryCount.Text = $"当前 {filtered.Count} 首 · 曲库共 {songs.Count} 首";
        RefreshAlphabet(); UpdateBatchControls(); updating = wasUpdating;
    }
    private void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!updating) { updating = true; FilterSongs(); updating = false; }
    }
    private async void SongSelected(object sender, SelectionChangedEventArgs e)
    {
        UpdateBatchControls();
        if (!multiSelect && !updating && SongsList.SelectedItem is SongItem song) await Run(() => SelectSong(song));
    }
    private async void PlayerSongChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updating && PlayerSong.SelectedItem is SongItem song) await Run(() => SelectSong(song));
    }
    private async Task SelectSong(SongItem song)
    {
        if (playbackState is "playing" or "countdown" or "practice" or "preview") await backend.Call("stop");
        await backend.Call("reset");
        int request = ++loadSequence;
        string profile = (ProfileBox.SelectedItem as ProfileItem)?.Id ?? "delta_force_harmonica";
        var result = await backend.Call("get", new { id = song.Id, profile_id = profile });
        if (request != loadSequence) return;
        selected = song; score = result;
        updating = true;
        PlayerSong.SelectedItem = song;
        if (!multiSelect && visibleSongs.Contains(song)) SongsList.SelectedItem = song;
        int count = result.GetProperty("notes").GetArrayLength();
        BpmBox.Value = song.Bpm; StartBox.Value = 1; EndBox.Value = Math.Max(count, 1);
        StartBox.Maximum = EndBox.Maximum = Math.Max(count, 1);
        TransposeBox.Value = 0;
        var prefs = result.GetProperty("preferences");
        if (prefs.TryGetProperty("bpm", out var bpm)) BpmBox.Value = bpm.GetDouble();
        if (prefs.TryGetProperty("transpose", out var tr)) TransposeBox.Value = tr.GetDouble();
        if (prefs.TryGetProperty("start", out var start)) StartBox.Value = Math.Clamp(start.GetInt32() + 1, 1, Math.Max(count, 1));
        if (prefs.TryGetProperty("end", out var end)) EndBox.Value = Math.Clamp(end.GetInt32(), 1, Math.Max(count, 1));
        if (prefs.TryGetProperty("latency", out var latency)) LatencyBox.Value = latency.GetDouble();
        if (prefs.TryGetProperty("humanize", out var human)) HumanizeSwitch.IsOn = human.GetBoolean();
        SelectedTitle.Text = song.Name;
        SelectedMeta.Text = song.Subtitle + $"\n{count} 个元素";
        double duration = result.GetProperty("notes").EnumerateArray().Sum(n => n.GetProperty("dur").GetDouble()) * 60 / song.Bpm;
        ScoreDescription.Text = $"{song.Name}  ·  {count} 个元素  ·  约 {duration / 60:0}:{duration % 60:00}";
        GoButton.IsEnabled = EditButton.IsEnabled = ExportButton.IsEnabled = DeleteButton.IsEnabled = PlayButton.IsEnabled = PreviewButton.IsEnabled = ResetButton.IsEnabled = true;
        updating = false;
        mini?.UpdateSongs(songs, selected);
        UpdateBatchControls();
        await backend.Call("prepare", PlaybackArgs());
        OnBackendEvent("status", await backend.Call("status"));
    }
    private async void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        ScenarioBox.IsEnabled = (ProfileBox.SelectedItem as ProfileItem)?.Id == "delta_force_harmonica";
        if (ready && !updating && selected is not null) await Run(() => SelectSong(selected));
    }
    private void GoClick(object sender, RoutedEventArgs e) => Nav.SelectedItem = Nav.MenuItems[1];
    private void SongDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Nav.SelectedItem = Nav.MenuItems[1];
    private Dictionary<string, object> PlaybackArgs()
    {
        if (selected is null) throw new InvalidOperationException("请先选择一首曲目。");
        static int Number(NumberBox box) => double.IsFinite(box.Value) && box.Value == Math.Truncate(box.Value) ? (int)box.Value : throw new InvalidOperationException("请输入有效的整数设置。");
        return new() { ["score_id"] = selected.Id, ["profile_id"] = ((ProfileItem)ProfileBox.SelectedItem).Id,
            ["bpm"] = Number(BpmBox), ["transpose"] = Number(TransposeBox), ["start"] = Number(StartBox), ["end"] = Number(EndBox),
            ["latency"] = Number(LatencyBox), ["humanize"] = HumanizeSwitch.IsOn,
            ["scenario"] = ((ComboBoxItem)ScenarioBox.SelectedItem).Tag.ToString()!, ["mode"] = ((ComboBoxItem)ModeBox.SelectedItem).Tag.ToString()!,
            ["own_hwnds"] = mini is null ? new long[] { Hwnd.ToInt64() } : new[] { Hwnd.ToInt64() }.Concat(mini.InputWindowHandles).ToArray() };
    }
    private Task StartPlayback() => Run(async () => { seekTimer.Stop(); await CommitSpeed(); await backend.Call("start", PlaybackArgs()); });
    private async void PlayClick(object sender, RoutedEventArgs e) => await StartPlayback();
    private async void PauseClick(object sender, RoutedEventArgs e) => await Run(async () => await backend.Call("stop"));
    private async void ResetClick(object sender, RoutedEventArgs e) => await Run(async () => await backend.Call("reset"));
    private Task TogglePreview() => Run(async () => { seekTimer.Stop(); await CommitSpeed(); await backend.Call("preview", PlaybackArgs()); });
    private async void PreviewClick(object sender, RoutedEventArgs e) => await TogglePreview();
    private void SpeedChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ready && !speedUpdating) ScheduleSpeed(e.NewValue);
    }
    private void ApplySpeed(double speed)
    {
        globalSpeed = Math.Clamp(speed, .5, 3);
        speedUpdating = true; SpeedSlider.Value = globalSpeed; speedUpdating = false;
        SpeedLabel.Text = $"全局倍速 · {globalSpeed:0.00}×";
        EffectiveBpmText.Text = $"实际 {BpmBox.Value * globalSpeed:0.##} BPM";
        mini?.SetSpeed(globalSpeed);
    }
    private void ScheduleSpeed(double value)
    {
        ApplySpeed(value); speedPending = true; speedTimer.Stop(); speedTimer.Start();
    }
    private async Task CommitSpeed()
    {
        speedTimer.Stop();
        if (!speedPending) return;
        double value = globalSpeed;
        await backend.Call("set_speed", new { speed = value });
        if (globalSpeed == value) speedPending = false;
    }
    private void SeekChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!ready || updating || !SeekSlider.IsEnabled) return;
        seekTimer.Stop(); seekTimer.Start();
    }
    private FileOpenPicker OpenPicker(params string[] extensions)
    {
        var picker = new FileOpenPicker(AppWindow.Id); foreach (string ext in extensions) picker.FileTypeFilter.Add(ext);
        return picker;
    }
    private async Task<string?> SavePicker(string name, string description, string extension)
    {
        var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = name };
        picker.FileTypeChoices.Add(description, new List<string> { extension });
        return (await picker.PickSaveFileAsync())?.Path;
    }
    private async void ImportClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var files = await OpenPicker(".json", ".mid", ".midi", ".zip").PickMultipleFilesAsync();
        if (files.Count == 0) return;
        var choice = await ChooseGroups("导入到分组"); if (choice is null) return;
        var result = await backend.Call("import", new { paths = files.Select(f => f.Path).ToArray(), group_ids = choice.Groups, favorite = choice.Favorite }, 300);
        ShowImportedGroup(choice);
        await RefreshLibrary(); await ImportReport(result);
    });
    private async void BatchClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var picker = new FolderPicker(AppWindow.Id);
        var folder = await picker.PickSingleFolderAsync(); if (folder is null) return;
        var choice = await ChooseGroups("批量导入到分组"); if (choice is null) return;
        var result = await backend.Call("import", new { folder = folder.Path, group_ids = choice.Groups, favorite = choice.Favorite }, 300);
        ShowImportedGroup(choice);
        await RefreshLibrary(); await ImportReport(result);
    });
    private async Task ImportReport(JsonElement result)
    {
        int count = result.GetProperty("count").GetInt32();
        var failures = ReportMessages(result, "errors").Concat(ReportMessages(result, "skipped")).ToList();
        var warnings = ReportMessages(result, "warnings");
        StatusText.Text = $"已导入 {count} 首曲目" + (failures.Count > 0 ? $" · 未导入 {failures.Count} 项" : "");
        if (failures.Count + warnings.Count > 0)
            await ShowOperationReport(count == 0 && failures.Count > 0 ? "导入未完成" : failures.Count > 0 ? "部分曲目未导入" : "导入完成",
                StatusText.Text, failures.Concat(warnings));
    }
    private async void ExportClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (selected is null) return;
        var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = selected.Name, DefaultFileExtension = ".json" };
        picker.FileTypeChoices.Add("JSON 乐谱", new List<string> { ".json" });
        picker.FileTypeChoices.Add("MIDI 乐谱", new List<string> { ".mid", ".midi" });
        var file = await picker.PickSaveFileAsync(); if (file is null) return;
        var path = file.Path;
        await backend.Call("export", new { id = selected.Id, path }); StatusText.Text = "乐谱已导出";
    });
    private async void DeleteClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (selected is null) return;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "删除曲目", Content = $"确认删除《{selected.Name}》？", PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await ShowDialog(dialog) != ContentDialogResult.Primary) return;
        await backend.Call("delete", new { id = selected.Id }); await RefreshLibrary();
    });
    private async void NewClick(object sender, RoutedEventArgs e) => await Run(() => Editor(null));
    private async void EditClick(object sender, RoutedEventArgs e) => await Run(() => Editor(selected));
    private async Task Editor(SongItem? song)
    {
        if (playbackState is "playing" or "countdown" or "practice" or "preview") throw new InvalidOperationException("请先暂停，再编辑乐谱。");
        var old = song is null ? default : await backend.Call("get", new { id = song.Id });
        var name = new TextBox { Header = "曲名", Text = song?.Name ?? "新曲目" };
        var bpm = new NumberBox { Header = "BPM", Minimum = 30, Maximum = 300, Value = song?.Bpm ?? 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var mode = new ComboBox { Header = "编辑格式", HorizontalAlignment = HorizontalAlignment.Stretch };
        mode.Items.Add("简谱文本"); mode.Items.Add("音符 JSON · 保留任意时值");
        var text = new TextBox { Header = "乐谱", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 250, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
        mode.SelectedIndex = song is null ? 0 : 1;
        string originalText = song is null ? "1 2 3 4 5 6 7 1'" : old.GetProperty("editor_text").GetString()!;
        string originalJson = song is null ? "[]" : JsonSerializer.Serialize(old.GetProperty("notes"), new JsonSerializerOptions { WriteIndented = true });
        text.Text = mode.SelectedIndex == 0 ? originalText : originalJson;
        mode.SelectionChanged += (_, _) => text.Text = mode.SelectedIndex == 0 ? originalText : originalJson;
        var error = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed), TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 14, Width = 560 }; content.Children.Add(name); content.Children.Add(bpm); content.Children.Add(mode); content.Children.Add(text);
        content.Children.Add(new TextBlock { Text = "简谱示例：1 2_ 3' 4, 5# [1 3 5]- 0。双高音 1 写为 1''；特殊拍数可写 3{1.25}。已有乐谱默认使用 JSON；切换格式会恢复原始内容。", TextWrapping = TextWrapping.Wrap, FontSize = 12 }); content.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = song is null ? "新建简谱" : "编辑乐谱", Content = content, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += async (_, ev) =>
        {
            var deferral = ev.GetDeferral(); dialog.IsPrimaryButtonEnabled = false;
            try
            {
                var values = new Dictionary<string, object> { ["id"] = song?.Id ?? 0, ["name"] = name.Text, ["bpm"] = (int)bpm.Value };
                if (song is null) { values["group_ids"] = currentGroup > 0 ? new[] { currentGroup } : new[] { 1 }; values["favorite"] = currentGroup == -1; }
                if (mode.SelectedIndex == 0) values["text"] = text.Text;
                else values["notes"] = JsonDocument.Parse(text.Text).RootElement.Clone();
                await backend.Call("save", values); await RefreshLibrary();
            }
            catch (Exception e) { ev.Cancel = true; error.Text = e.Message; }
            finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        await ShowDialog(dialog);
    }
    private async void ImportDatabaseClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var file = await OpenPicker(".db").PickSingleFileAsync(); if (file is null) return;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "导入旧版曲库", Content = "会把旧库的全部曲目追加到当前曲库，并先备份当前数据库。重复导入会产生重复曲目。", PrimaryButtonText = "导入", CloseButtonText = "取消" };
        if (await ShowDialog(dialog) != ContentDialogResult.Primary) return;
        var choice = await ChooseGroups("导入旧曲库到分组"); if (choice is null) return;
        var result = await backend.Call("import_database", new { path = file.Path, group_ids = choice.Groups, favorite = choice.Favorite }, 300);
        ShowImportedGroup(choice);
        await RefreshLibrary(); StatusText.Text = $"已导入旧版曲库 {result.GetProperty("count")} 首曲目";
    });
    private async Task RefreshLogs()
    {
        var logs = await backend.Call("logs"); LogList.ItemsSource = JsonSerializer.Deserialize<List<LogItem>>(logs);
    }
    private async void RefreshLogsClick(object sender, RoutedEventArgs e) => await Run(RefreshLogs);
    private async void LogSelected(object sender, SelectionChangedEventArgs e) => await Run(async () =>
    {
        if (LogList.SelectedItem is not LogItem log) return;
        var details = await backend.Call("log_detail", new { file = log.File });
        LogDetail.Text = $"{log.Name} · {details.GetProperty("count")} 条事件（显示最近 2000 条）\n\n" + details.GetProperty("text").GetString();
    });
    private async void ExportLogClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (LogList.SelectedItem is not LogItem log) return;
        string ext = ((Button)sender).Tag.ToString()!;
        var path = await SavePicker(Path.GetFileNameWithoutExtension(log.File), "演奏记录", "." + ext); if (path is null) return;
        await backend.Call("log_export", new { file = log.File, path }); StatusText.Text = "演奏记录已导出";
    });
    private void ApplyTheme()
    {
        if (Root is null) return;
        Root.RequestedTheme = savedTheme == 1 ? ElementTheme.Light : savedTheme == 2 ? ElementTheme.Dark : ElementTheme.Default;
    }
    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        savedTheme = ThemeBox.SelectedIndex; ApplyTheme(); if (ready && !selfTest) SaveSettings();
    }
    private void DataFolderClick(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("explorer.exe", backend.DataDirectory) { UseShellExecute = true });
    private async void LicensesClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");
        await ShowDialog(new ContentDialog { XamlRoot = Root.XamlRoot, Title = "开源许可与第三方声明", Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = await File.ReadAllTextAsync(path), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 540 } }, CloseButtonText = "关闭" });
    });
    private async void MiniClick(object sender, RoutedEventArgs e) => await Run(ShowMini);
    private async Task ShowMini()
    {
        if (mini is not null) return;
        mini = new MiniWindow(this, songs, selected, Root.ActualTheme,
            () => _ = StartPlayback(), () => _ = Run(async () => await backend.Call("stop")), TogglePreview,
            SelectSong, async position =>
            {
                var values = PlaybackArgs(); values["position"] = position;
                await backend.Call("seek_selected", values);
            }, globalSpeed, ScheduleSpeed, () => mini = null);
        mini.ShowWithoutActivation(); AppWindow.Hide();
        OnBackendEvent("status", await backend.Call("status"));
    }
    private async void WindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (closing) return;
        args.Cancel = true; closing = true; updateCancellation.Cancel(); seekTimer.Stop(); speedTimer.Stop();
        mini?.CloseForShutdown();
        await backend.DisposeAsync();
        Close();
    }
    private async Task SelfTest()
    {
        var checks = new List<string>();
        if (Environment.GetCommandLineArgs().Contains("--picker-self-test"))
        {
            await PickerSelfTest.Check(AppWindow.Id);
            checks.Add("administrator-compatible open/save/folder pickers render and cancel cleanly");
        }
        await CheckAdministratorControls(checks);
        // Uses a recording driver and isolated data directory; creates no real keyboard/mouse input.
        if (LibraryPage.ActualWidth < 600 || Root.ActualHeight < 500) throw new Exception("WinUI layout did not render");
        checks.Add("native WinUI window rendered");
        var saved = await backend.Call("save", new { name = "原生界面自检曲目", bpm = 120, text = "1 2_ 3' 4, 5# [1 3 5]- 0 1''" });
        int id = saved.GetProperty("id").GetInt32();
        await RefreshLibrary();
        if (!songs.Any(s => s.Id == id)) throw new Exception("library refresh failed");
        await SelectSong(songs.First(s => s.Id == id));
        if (!score.GetProperty("notes").EnumerateArray().Any(note => note.GetProperty("notes").EnumerateArray().Any(pitch => pitch.GetString() == "top_1")) ||
            !score.GetProperty("editor_text").GetString()!.Contains("1''")) throw new Exception("Double-high tonic was lost in native RPC or editor text");
        checks.Add("double-high tonic persists through native save, selection and text editing");
        checks.Add("library list and selection");
        SearchBox.Text = "自检"; FilterSongs();
        if (((List<SongItem>)SongsList.ItemsSource).Count != 1) throw new Exception("search failed");
        SearchBox.Text = ""; FilterSongs(); checks.Add("song search");
        await CheckLibraryManagement(checks, id);
        await CheckTransportPlacement(checks);
        Nav.SelectedItem = Nav.MenuItems[1];
        await Task.Delay(300);
        if (PlayerPage.Visibility != Visibility.Visible || !PlayButton.IsEnabled || PlayerPage.ActualHeight <= 0) throw new Exception("player page failed");
        checks.Add("player navigation and native controls");
        var leftBounds = PlaybackSettingsCard.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0));
        var rightBounds = RhythmSettingsCard.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0));
        if (rightBounds.X < leftBounds.X + PlaybackSettingsCard.ActualWidth - 1 && rightBounds.Y < leftBounds.Y + PlaybackSettingsCard.ActualHeight - 1)
            throw new Exception("player settings cards overlap");
        checks.Add("player settings cards have non-overlapping bounds");
        await UiSnapshot.Save(Root, Path.Combine(Path.GetDirectoryName(selfTestFile!)!, "main-preview.png"));
        await backend.Call("prepare", PlaybackArgs());
        await backend.Call("seek", new { position = 2 });
        await Task.Delay(150);
        if (SeekSlider.Value != 2) throw new Exception("progress event binding failed");
        checks.Add("RPC status and progress binding");
        Nav.SelectedItem = Nav.MenuItems[2]; await RefreshLogs(); checks.Add("logs navigation");
        Nav.SelectedItem = Nav.SettingsItem; ThemeBox.SelectedIndex = 2;
        if (!AutoUpdateToggle.IsOn || !CheckUpdateButton.IsEnabled || InstallUpdateButton.IsEnabled) throw new Exception("Update settings controls failed");
        AutoUpdateToggle.IsOn = false; SaveSettings();
        using (var updateSettings = JsonDocument.Parse(File.ReadAllText(settingsFile)))
            if (updateSettings.RootElement.GetProperty("autoUpdate").GetBoolean()) throw new Exception("Update setting did not persist");
        AutoUpdateToggle.IsOn = true; SaveSettings();
        checks.Add("settings, dark theme and persisted automatic update controls");
        var export = Path.Combine(backend.DataDirectory, "self-test-export.json");
        await backend.Call("export", new { id, path = export });
        if (!File.Exists(export)) throw new Exception("export failed"); checks.Add("JSON export");
        await CheckArchives(checks, id);
        var midiExport = Path.Combine(backend.DataDirectory, "self-test-export.mid");
        var beforeMidiIds = songs.Select(s => s.Id).ToHashSet();
        await backend.Call("export", new { id, path = midiExport });
        var midiImport = await backend.Call("import", new { paths = new[] { midiExport } });
        if (!File.Exists(midiExport) || midiImport.GetProperty("count").GetInt32() != 1) throw new Exception("MIDI import/export failed");
        // Remove the imported duplicate so previous/next assertions keep a two-song library.
        var listed = await backend.Call("list");
        foreach (var item in listed.EnumerateArray()) if (!beforeMidiIds.Contains(item.GetProperty("id").GetInt32())) await backend.Call("delete", new { id = item.GetProperty("id").GetInt32() });
        await RefreshLibrary(); checks.Add("MIDI export and reimport through native RPC");
        await ShowMini();
        await Task.Delay(300);
        if (mini is null || !mini.HasRendered || !mini.DoesNotActivate) throw new Exception("mini window focus protection or rendering failed");
        // The main window was hidden; opening the small window must never make it foreground.
        if (NonActivatingSearch.GetForegroundWindow() == mini.Hwnd) throw new Exception("mini window stole foreground focus");
        checks.Add("mini rendered with WS_EX_NOACTIVATE and MA_NOACTIVATE");
        if (mini.ContentHeight > 200) throw new Exception("mini player is too large");
        mini.CheckBounds();
        await UiSnapshot.Save((FrameworkElement)mini.Content, Path.Combine(Path.GetDirectoryName(selfTestFile!)!, "mini-preview.png"));
        checks.Add("compact player content fits within 200 logical pixels height");
        await mini.TogglePreview(); await Task.Delay(150);
        var previewStatus = await backend.Call("status");
        if (previewStatus.GetProperty("state").GetString() != "preview" || !mini.IsPreviewing || PreviewButton.Content?.ToString() != "停止试听")
            throw new Exception("mini preview did not synchronize the library transport");
        if (NonActivatingSearch.GetForegroundWindow() == mini.Hwnd) throw new Exception("preview stole foreground focus");
        await mini.TogglePreview(); await Task.Delay(150);
        if ((await backend.Call("status")).GetProperty("state").GetString() == "preview" || mini.IsPreviewing)
            throw new Exception("mini preview did not stop");
        checks.Add("mini audition starts/stops local piano, synchronizes library controls and preserves foreground");
        mini.OpenPicker(); await Task.Delay(250);
        var picker = mini.Picker ?? throw new Exception("song picker did not open");
        if (!picker.HasRendered || !picker.DoesNotActivate || NonActivatingSearch.GetForegroundWindow() == picker.Hwnd) throw new Exception("song picker stole focus or did not render");
        await UiSnapshot.Save((FrameworkElement)picker.Content, Path.Combine(Path.GetDirectoryName(selfTestFile!)!, "song-picker-preview.png"));
        picker.SetQuery("yuanshengjiemian");
        if (picker.VisibleSongCount != 1) throw new Exception("pinyin search failed");
        picker.SetQuery("ysjmzjqm");
        if (picker.VisibleSongCount != 1) throw new Exception("pinyin initials search failed");
        picker.SetQuery("自检"); if (picker.VisibleSongCount != 1) throw new Exception("Chinese search failed");
        picker.SetQuery("不存在的歌名");
        if (picker.VisibleSongCount != 0) throw new Exception("empty search failed");
        picker.SetQuery(""); checks.Add("separate non-activating picker: Chinese, full pinyin, initials and empty search");
        var another = await backend.Call("save", new { name = "小窗切歌测试", bpm = 90, text = "5 3 1" });
        int otherId = another.GetProperty("id").GetInt32(); await RefreshLibrary();
        await picker.SelectSong(songs.First(s => s.Id == otherId));
        if (selected?.Id != otherId || (PlayerSong.SelectedItem as SongItem)?.Id != otherId || BpmBox.Value != 90)
            throw new Exception("mini selection did not synchronize player");
        if (NonActivatingSearch.GetForegroundWindow() == mini.Hwnd) throw new Exception("song selection stole focus");
        checks.Add("mini selection updates player and BPM without activating");
        if (mini.Picker is not null) throw new Exception("picker did not close after selection");
        await mini.ChangeSong(1); if (selected?.Id != id) throw new Exception("next song failed");
        await mini.ChangeSong(-1); if (selected?.Id != otherId) throw new Exception("previous song failed");
        await mini.SeekTo(2); var seekStatus = await backend.Call("status");
        if (seekStatus.GetProperty("position").GetInt32() != 2 || seekStatus.GetProperty("state").GetString() == "playing") throw new Exception("mini seek failed");
        checks.Add("mini previous/next and seek update transport and leave playback paused");
        mini.AdjustSpeed(2.237); await CommitSpeed();
        var speedStatus = await backend.Call("status");
        if (Math.Abs(SpeedSlider.Value - 2.237) > .001 || Math.Abs(speedStatus.GetProperty("speed").GetDouble() - 2.237) > .001)
            throw new Exception("global speed did not synchronize mini, main and engine");
        checks.Add("continuous global rate from mini synchronizes main, engine and persisted configuration");
        NonActivatingSearch.VerifyCapture();
        checks.Add("search hook suppresses typed key pairs, ignores injected playback and preserves foreground");
        mini.Close(); await backend.Call("delete", new { id = otherId });
        await backend.Call("delete", new { id });
        File.WriteAllText(selfTestFile!, JsonSerializer.Serialize(new { passed = true, checks, framework = "Microsoft.UI.Xaml / WinUI 3", version = "2.2.2" }, new JsonSerializerOptions { WriteIndented = true }));
        Close();
    }
}

