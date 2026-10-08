using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace AutoMusicPlayer;

public sealed class MiniWindow : NonActivatingWindow
{
    private readonly TextBlock title = new() { Text = "选择一首曲目", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock message = new() { Text = "F6 开始 · F8 暂停", FontSize = 11, Opacity = .7, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock time = new() { Text = "0:00 / 0:00", FontSize = 11, Opacity = .65 };
    private readonly Slider progress = new() { Minimum = 0, Maximum = 1, StepFrequency = 1, IsEnabled = false, IsTabStop = false, Height = 24, MinHeight = 24 };
    private readonly Slider speedSlider = new() { Minimum = .5, Maximum = 3, Value = 1, StepFrequency = .001, SmallChange = .01, Height = 24, MinHeight = 24, IsTabStop = false };
    private readonly Button speedLabel = new() { Content = "1.00×", Padding = new Thickness(6, 0, 6, 0), Height = 26, MinHeight = 26, IsTabStop = false, FontSize = 11 };
    private bool speedUpdating;
    private readonly SymbolIcon playIcon = new(Symbol.Play) { Symbol = Symbol.Play };
    private readonly Button playButton;
    private readonly Button previous;
    private readonly Button next;
    private readonly Func<SongItem, Task> choose;
    private readonly Func<int, Task> seek;
    private readonly Action pause;
    private readonly Action play;
    private readonly MainWindow parent;
    private readonly DispatcherTimer seekTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly ElementTheme theme;
    private List<SongItem> songs = [];
    private SongItem? selected;
    private SongPickerWindow? picker;
    private bool restoring, shuttingDown, updating, selecting, active, manualSeek;
    internal SongPickerWindow? Picker => picker;
    public long[] InputWindowHandles => picker is null ? [Hwnd.ToInt64()] : [Hwnd.ToInt64(), picker.Hwnd.ToInt64()];
    internal double ContentHeight => ((FrameworkElement)Content).ActualHeight;
    public MiniWindow(MainWindow parent, List<SongItem> songs, SongItem? selected, ElementTheme theme,
                      Action play, Action pause, Func<SongItem, Task> choose, Func<int, Task> seek, double speed, Action<double> changeSpeed, Action restore)
    {
        this.parent = parent; this.play = play; this.pause = pause; this.choose = choose; this.seek = seek; this.theme = theme;
        Title = "演奏小窗";
        var root = new Grid { Padding = new Thickness(16, 10, 16, 12), RowSpacing = 4, RequestedTheme = theme, Background = Surface(theme) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) root.RowDefinitions.Add(new RowDefinition { Height = height });
        var songRow = new Grid { ColumnSpacing = 12 };
        songRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); songRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        songRow.Children.Add(title); var chevron = new FontIcon { Glyph = "\uE70D", FontSize = 11, Opacity = .65 }; Grid.SetColumn(chevron, 1); songRow.Children.Add(chevron);
        var songButton = new Button { Content = songRow, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 8, 10, 8), IsTabStop = false };
        ToolTipService.SetToolTip(songButton, "选择曲目 / 搜索"); songButton.Click += (_, _) => OpenPicker(); root.Children.Add(songButton);
        Grid.SetRow(progress, 1); progress.Margin = new Thickness(0, -3, 0, -3); root.Children.Add(progress);
        var status = new Grid { ColumnSpacing = 8 }; Grid.SetRow(status, 2); status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); status.Children.Add(message); Grid.SetColumn(time, 1); status.Children.Add(time); root.Children.Add(status);
        var controls = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 6, 0, 0) }; Grid.SetRow(controls, 3);
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) controls.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        previous = IconButton(Symbol.Previous, "上一曲", 36); Grid.SetColumn(previous, 1); previous.Click += async (_, _) => await ChangeSong(-1); controls.Children.Add(previous);
        playButton = new Button { Content = playIcon, Width = 44, Height = 44, CornerRadius = new CornerRadius(22), IsTabStop = false, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        Grid.SetColumn(playButton, 2); playButton.Click += async (_, _) => { picker?.Close(); seekTimer.Stop(); if (manualSeek) { await SeekTo((int)progress.Value); manualSeek = false; } if (active) pause(); else play(); }; controls.Children.Add(playButton); ToolTipService.SetToolTip(playButton, "播放 / 暂停 · F6 / F8");
        next = IconButton(Symbol.Next, "下一曲", 36); Grid.SetColumn(next, 3); next.Click += async (_, _) => await ChangeSong(1); controls.Children.Add(next);
        var back = IconButton(Symbol.BackToWindow, "返回主窗口", 30); back.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(back, 4); back.Click += (_, _) => { restoring = true; Close(); }; controls.Children.Add(back); root.Children.Add(controls);
        var speedRow = new Grid { ColumnSpacing = 10 }; Grid.SetRow(speedRow, 4); speedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); speedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); speedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        speedRow.Children.Add(new TextBlock { Text = "倍速", FontSize = 11, Opacity = .65, VerticalAlignment = VerticalAlignment.Center }); Grid.SetColumn(speedSlider, 1); speedRow.Children.Add(speedSlider); Grid.SetColumn(speedLabel, 2); speedRow.Children.Add(speedLabel); root.Children.Add(speedRow);
        ToolTipService.SetToolTip(speedSlider, "全局倍速 · 0.5–3.0× · 演奏和试听同时生效"); ToolTipService.SetToolTip(speedLabel, "点击恢复 1×");
        speedSlider.ValueChanged += (_, e) => { if (!speedUpdating) changeSpeed(e.NewValue); }; speedLabel.Click += (_, _) => changeSpeed(1);
        Content = root; SystemBackdrop = new DesktopAcrylicBackdrop(); Configure(340, 198);
        seekTimer.Tick += async (_, _) => { seekTimer.Stop(); await SeekTo((int)progress.Value); manualSeek = false; };
        progress.ValueChanged += (_, _) => { if (!updating && progress.IsEnabled) { manualSeek = true; pause(); seekTimer.Stop(); seekTimer.Start(); } };
        Closed += (_, _) => { shuttingDown = true; seekTimer.Stop(); picker?.Close(); if (!shuttingDownFromParent) parent.AppWindow.Show(restoring); restore(); };
        UpdateSongs(songs, selected);
        SetSpeed(speed);
    }
    private bool shuttingDownFromParent;
    internal static SolidColorBrush Surface(ElementTheme theme) => new(theme == ElementTheme.Light ? Microsoft.UI.ColorHelper.FromArgb(255, 249, 249, 250) : Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 36));
    internal static Button IconButton(Symbol symbol, string hint, int size)
    {
        var button = new Button { Content = new SymbolIcon(symbol) { Width = 20, Height = 20 }, Width = size, Height = size, Padding = new Thickness(0), CornerRadius = new CornerRadius(size / 2.0), IsTabStop = false };
        ToolTipService.SetToolTip(button, hint); return button;
    }
    public void CloseForShutdown() { shuttingDownFromParent = true; Close(); }
    public void ShowMessage(string text) => message.Text = text;
    public void SetSpeed(double value) { speedUpdating = true; speedSlider.Value = value; speedLabel.Content = $"{value:0.00}×"; speedUpdating = false; }
    internal void AdjustSpeed(double value) => speedSlider.Value = value;
    internal void CheckBounds()
    {
        var root = (FrameworkElement)Content;
        foreach (var item in new FrameworkElement[] { title, progress, playButton, previous, next, speedSlider, speedLabel })
        {
            var p = item.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point());
            if (item.ActualWidth <= 0 || item.ActualHeight <= 0 || p.X < 0 || p.Y < 0 || p.X + item.ActualWidth > root.ActualWidth + 1 || p.Y + item.ActualHeight > root.ActualHeight + 1)
                throw new Exception("compact player control is clipped: " + item.GetType().Name);
        }
    }
    public void UpdateSongs(List<SongItem> values, SongItem? song)
    {
        songs = values; selected = song; title.Text = song?.Name ?? "选择一首曲目";
        playButton.IsEnabled = song is not null && !selecting; previous.IsEnabled = next.IsEnabled = songs.Count > 1 && !selecting;
        picker?.UpdateSongs(songs, selected);
    }
    internal void OpenPicker()
    {
        if (picker is not null || shuttingDown) return;
        picker = new SongPickerWindow(this, songs, selected, theme, pause, SelectSong);
        picker.Closed += (_, _) => picker = null;
        picker.ShowWithoutActivation();
    }
    internal async Task SelectSong(SongItem song)
    {
        if (selecting) return;
        selecting = true; seekTimer.Stop(); manualSeek = false; UpdateSongs(songs, selected);
        try { await choose(song); selected = song; message.Text = "已切换 · F6 开始"; }
        finally { selecting = false; UpdateSongs(songs, selected); }
    }
    internal async Task ChangeSong(int direction)
    {
        if (songs.Count == 0 || selecting) return;
        int index = selected is null ? (direction > 0 ? -1 : 0) : songs.FindIndex(s => s.Id == selected.Id);
        int target = (index + direction + songs.Count) % songs.Count;
        try { await SelectSong(songs[target]); picker?.Close(); }
        catch (Exception exc) { ShowMessage(exc.Message); }
    }
    internal async Task SeekTo(int position)
    {
        try { await seek(position); message.Text = "已跳转 · F6 继续"; }
        catch (Exception exc) { ShowMessage(exc.Message); }
    }
    public void Update(string text, int position, int total, string state, double elapsed, double duration)
    {
        active = state is "playing" or "countdown" or "practice" or "preview";
        playIcon.Symbol = active ? Symbol.Pause : Symbol.Play;
        updating = true; progress.Maximum = Math.Max(total, 1); if (!manualSeek) progress.Value = Math.Clamp(position, 0, Math.Max(total, 1)); progress.IsEnabled = total > 0 && !selecting; updating = false;
        static string Clock(double seconds) => $"{(int)Math.Max(0, seconds) / 60}:{(int)Math.Max(0, seconds) % 60:00}";
        time.Text = $"{Clock(elapsed)} / {Clock(duration)}";
        message.Text = text;
    }
}
