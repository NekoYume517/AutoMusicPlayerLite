using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Windows.ApplicationModel.DataTransfer;
namespace AutoMusicPlayer;

public sealed class SongPickerWindow : NonActivatingWindow
{
    private readonly TextBlock searchLabel = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock help = new() { FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock empty = new() { Text = "没有匹配的曲目", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = .6 };
    private readonly ListView list = new() { IsTabStop = false, SelectionMode = ListViewSelectionMode.Single, DisplayMemberPath = "Name" };
    private readonly NonActivatingSearch input;
    private readonly DispatcherTimer timeout;
    private readonly Func<SongItem, Task> choose;
    private readonly Action pause;
    private List<SongItem> songs = [];
    private int? selectedId;
    private string query = "";
    private bool updating, selecting;
    internal int VisibleSongCount => ((List<SongItem>)list.ItemsSource).Count;
    public SongPickerWindow(MiniWindow owner, List<SongItem> values, SongItem? selected, ElementTheme theme, Action pause, Func<SongItem, Task> choose)
    {
        this.pause = pause; this.choose = choose; Title = "选择曲目";
        input = new NonActivatingSearch(command => DispatcherQueue.TryEnqueue(() => SearchCommand(command)));
        timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) }; timeout.Tick += (_, _) => EndSearch();
        var root = new Grid { RequestedTheme = theme, Background = MiniWindow.Surface(theme), Padding = new Thickness(16), RowSpacing = 12 };
        foreach (var size in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) root.RowDefinitions.Add(new RowDefinition { Height = size });
        var header = new StackPanel { Spacing = 4 }; header.Children.Add(new TextBlock { Text = "选择曲目", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }); header.Children.Add(new TextBlock { Text = "选好即收起，游戏保持前台", FontSize = 12, Opacity = .65 }); root.Children.Add(header);
        var search = new Grid { ColumnSpacing = 6 }; Grid.SetRow(search, 1); search.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); search.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); search.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var entry = new Button { Content = searchLabel, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, IsTabStop = false };
        entry.Click += (_, _) => { if (input.IsActive) EndSearch(); else BeginSearch(); }; search.Children.Add(entry);
        var paste = MiniWindow.IconButton(Symbol.Paste, "粘贴中文曲名", 32); Grid.SetColumn(paste, 1); paste.Click += async (_, _) => await Paste(); search.Children.Add(paste);
        var clear = MiniWindow.IconButton(Symbol.Clear, "清空搜索", 32); Grid.SetColumn(clear, 2); clear.Click += (_, _) => SetQuery(""); search.Children.Add(clear); root.Children.Add(search);
        var songList = new Grid(); Grid.SetRow(songList, 2); songList.Children.Add(list); songList.Children.Add(empty); root.Children.Add(songList);
        Grid.SetRow(help, 3); root.Children.Add(help); Content = root;
        list.SelectionChanged += async (_, _) => { if (!updating && !selecting && list.SelectedItem is SongItem song && song.Id != selectedId) await SelectSong(song); };
        // Clicking the already-current row should also dismiss the chooser.
        list.IsItemClickEnabled = true;
        list.ItemClick += (_, e) => { if (e.ClickedItem is SongItem song && song.Id == selectedId && !selecting) Close(); };
        Configure(360, 400, owner);
        var area = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int x = Math.Clamp(owner.AppWindow.Position.X, area.X, Math.Max(area.X, area.X + area.Width - AppWindow.Size.Width));
        int y = owner.AppWindow.Position.Y + owner.AppWindow.Size.Height + 6;
        if (y + AppWindow.Size.Height > area.Y + area.Height) y = owner.AppWindow.Position.Y - AppWindow.Size.Height - 6;
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - AppWindow.Size.Height));
        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        Closed += (_, _) => { EndSearch(); input.Dispose(); };
        UpdateSongs(values, selected);
    }
    internal void UpdateSongs(List<SongItem> values, SongItem? selected) { songs = values; selectedId = selected?.Id; Filter(); }
    private void Filter()
    {
        updating = true; var matches = songs.Where(s => s.Matches(query.Trim())).ToList(); list.ItemsSource = matches; list.SelectedItem = matches.FirstOrDefault(s => s.Id == selectedId); empty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        searchLabel.Text = (query.Length == 0 ? "搜索曲名 / 拼音 / 首字母" : query) + (input.IsActive ? " ▏" : "");
        help.Text = input.IsActive ? "搜索中 · Enter/Esc 结束 · Ctrl+V 粘贴中文" : $"{matches.Count} 首曲目 · 点击搜索后输入拼音，也可粘贴中文";
        updating = false;
    }
    internal void SetQuery(string text) { query = text.Length > 100 ? text[..100] : text; Filter(); }
    internal async Task SelectSong(SongItem song)
    {
        if (selecting) return;
        EndSearch(); selecting = true; list.IsEnabled = false;
        try { await choose(song); selectedId = song.Id; Close(); }
        catch (Exception exc) { help.Text = exc.Message; }
        finally { selecting = false; list.IsEnabled = true; }
    }
    private void BeginSearch() { try { pause(); input.Begin(); timeout.Stop(); timeout.Start(); Filter(); } catch (Exception exc) { help.Text = exc.Message; } }
    private void EndSearch() { input.End(); timeout.Stop(); Filter(); }
    private async void SearchCommand(string command)
    {
        if (!input.IsActive) return;
        timeout.Stop(); timeout.Start();
        if (command == "end") EndSearch();
        else if (command == "paste") await Paste();
        else if (command == "clear") SetQuery("");
        else if (command == "backspace") SetQuery(query.Length == 0 ? "" : query[..^(char.IsLowSurrogate(query[^1]) && query.Length > 1 ? 2 : 1)]);
        else if (command.StartsWith("text:")) SetQuery(query + command[5..]);
        else if (command is "previous" or "next") { if (VisibleSongCount > 0) list.SelectedIndex = Math.Clamp(list.SelectedIndex + (command == "next" ? 1 : -1), 0, VisibleSongCount - 1); }
    }
    private async Task Paste() { try { var c = Clipboard.GetContent(); if (c.Contains(StandardDataFormats.Text)) SetQuery(await c.GetTextAsync()); } catch (Exception exc) { help.Text = "无法读取剪贴板：" + exc.Message; } }
}
