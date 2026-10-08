using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoMusicPlayer;
public sealed partial class MainWindow
{
    private readonly SemaphoreSlim contentDialogs = new(1, 1);
    private Task<ContentDialogResult> ShowDialog(ContentDialog dialog) => ShowTestableDialog(dialog, null, "");
    private async Task<ContentDialogResult> ShowTestableDialog(ContentDialog dialog, FrameworkElement? content, string filename)
    {
        await contentDialogs.WaitAsync();
        try
        {
            if (closing) return ContentDialogResult.None;
            return await ShowDialogCore(dialog, content, filename);
        }
        finally { contentDialogs.Release(); }
    }
    private async Task<ContentDialogResult> ShowDialogCore(ContentDialog dialog, FrameworkElement? content, string filename)
    {
        if (!selfTest || content is null) return await dialog.ShowAsync();
        var captured = new TaskCompletionSource();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                if (content.ActualWidth <= 0 || content.ActualHeight <= 0) throw new Exception("Dialog content did not render");
                await UiSnapshot.Save(content, Path.Combine(Path.GetDirectoryName(selfTestFile!)!, filename));
                captured.SetResult();
            }
            catch (Exception ex) { captured.SetException(ex); }
            finally { dialog.Hide(); }
        };
        timer.Start();
        try
        {
            var result = await dialog.ShowAsync();
            await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return result;
        }
        finally { timer.Stop(); }
    }

    private async Task CheckLibraryManagement(List<string> checks, int originalId)
    {
        await CheckOperationReports(checks);
        int group = (await backend.Call("group_save", new { name = "自检分组" })).GetProperty("id").GetInt32();
        int otherGroup = (await backend.Call("group_save", new { name = "自检第二组" })).GetProperty("id").GetInt32();
        await backend.Call("library_batch", new { ids = new[] { originalId }, group_ids = new[] { group, otherGroup }, favorite = true });
        currentGroup = group;
        await RefreshLibrary();
        if (visibleSongs.Count != 1 || !visibleSongs[0].GroupIds.Contains(otherGroup) || LibraryTabs.TabItems.Count != 5) throw new Exception("Group tabs or multiple membership failed");
        checks.Add("library tabs and multiple group memberships persist and filter correctly");
        currentGroup = -1; RefreshTabs(); FilterSongs();
        if (visibleSongs.Count != 1 || !visibleSongs[0].Favorite || visibleSongs[0].FavoriteMark != "♥") throw new Exception("Favorites tab failed");
        checks.Add("favorites tab and favorite indicator");
        var choice = await ChooseGroups("导入分组预览", new[] { 1, group, otherGroup }, true);
        if (choice is not null) throw new Exception("Group dialog cancel failed");
        checks.Add("native multi-group import chooser renders and cancels cleanly");
        int alpha = (await backend.Call("save", new { name = "阿尔法排序自检", bpm = 100, text = "1 2" })).GetProperty("id").GetInt32();
        currentGroup = 0; await RefreshLibrary(); SortBox.SelectedIndex = 4;
        if (visibleSongs[0].Id != alpha || visibleSongs[0].NameInitial != "A" || AlphabetRail.Visibility != Visibility.Visible || AlphabetButtons.Children.Count != 27) throw new Exception("Pinyin ascending sort or alphabet index failed");
        SortBox.SelectedIndex = 5;
        if (visibleSongs[^1].Id != alpha) throw new Exception("Pinyin descending sort failed");
        SortBox.SelectedIndex = 1;
        if (AlphabetRail.Visibility != Visibility.Collapsed) throw new Exception("Alphabet should only appear for name sorting");
        checks.Add("pinyin ascending/descending ordering and contextual A-Z/# index");
        MultiSelectButton.IsChecked = true; MultiSelectClick(MultiSelectButton, new RoutedEventArgs());
        SongsList.SelectAll();
        if (BatchIds().Length != 2 || !BatchGroupButton.IsEnabled || BatchToolbar.Visibility != Visibility.Visible || EditButton.IsEnabled) throw new Exception("Multi-select controls failed");
        ClearSelectionClick(SongsList, new RoutedEventArgs());
        if (BatchIds().Length != 0) throw new Exception("Selection clear failed");
        MultiSelectButton.IsChecked = false; MultiSelectClick(MultiSelectButton, new RoutedEventArgs());
        checks.Add("multi-selection selects visible songs, exposes batch controls and clears correctly");
        var firstContent = new TextBlock { Text = "第一个排队弹窗", Width = 300 };
        var secondContent = new TextBlock { Text = "第二个排队弹窗", Width = 300 };
        var firstDialog = new ContentDialog { XamlRoot = Root.XamlRoot, Content = firstContent, CloseButtonText = "关闭" };
        var secondDialog = new ContentDialog { XamlRoot = Root.XamlRoot, Content = secondContent, CloseButtonText = "关闭" };
        await Task.WhenAll(ShowTestableDialog(firstDialog, firstContent, "modal-first.png"), ShowTestableDialog(secondDialog, secondContent, "modal-second.png"));
        checks.Add("concurrent content dialogs are serialized without modal conflicts");
        await backend.Call("delete", new { id = alpha });
        await backend.Call("group_delete", new { id = group });
        await backend.Call("group_delete", new { id = otherGroup });
        await backend.Call("library_batch", new { ids = new[] { originalId }, favorite = false });
        currentGroup = 1; SortBox.SelectedIndex = 1; await RefreshLibrary();
        await SelectSong(songs.First(s => s.Id == originalId));
    }
}
