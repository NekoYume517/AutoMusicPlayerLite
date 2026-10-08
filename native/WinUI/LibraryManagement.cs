using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AutoMusicPlayer;
public sealed partial class MainWindow
{
    private List<LibraryGroup> libraryGroups = [];
    private int currentGroup = 1;
    private bool changingTabs;
    private bool multiSelect;
    private int savedSort = 1;
    private List<SongItem> visibleSongs = [];
    private sealed record GroupChoice(int[] Groups, bool Favorite);

    private void RefreshTabs()
    {
        changingTabs = true;
        LibraryTabs.TabItems.Clear();
        var tabs = new List<(int Id, string Name, int Count)> { (0, "全部", songs.Count), (1, "默认分组", libraryGroups.FirstOrDefault(g => g.Id == 1)?.Count ?? 0), (-1, "收藏", songs.Count(s => s.Favorite)) };
        tabs.AddRange(libraryGroups.Where(g => g.Id != 1).Select(g => (g.Id, g.Name, g.Count)));
        foreach (var group in tabs)
        {
            var tab = new TabViewItem { Header = $"{group.Name}  {group.Count}", Tag = group.Id, IsClosable = false };
            if (group.Id > 1)
            {
                var flyout = new MenuFlyout();
                var rename = new MenuFlyoutItem { Text = "重命名分组" };
                rename.Click += async (_, _) => await Run(() => GroupEditor(libraryGroups.First(g => g.Id == group.Id)));
                var delete = new MenuFlyoutItem { Text = "删除分组（保留曲目）" };
                delete.Click += async (_, _) => await Run(async () =>
                {
                    var confirm = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "删除分组", Content = $"删除「{group.Name}」分组？曲目、收藏和其他分组会保留。", PrimaryButtonText = "删除分组", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
                    if (await ShowDialog(confirm) != ContentDialogResult.Primary) return;
                    await backend.Call("group_delete", new { id = group.Id });
                    if (currentGroup == group.Id) currentGroup = 1;
                    await RefreshLibrary();
                });
                flyout.Items.Add(rename); flyout.Items.Add(delete); tab.ContextFlyout = flyout;
            }
            LibraryTabs.TabItems.Add(tab);
        }
        LibraryTabs.SelectedItem = LibraryTabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => (int)t.Tag == currentGroup) ?? LibraryTabs.TabItems[1];
        currentGroup = (int)((TabViewItem)LibraryTabs.SelectedItem).Tag;
        changingTabs = false;
    }
    private void LibraryTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingTabs || !ready || LibraryTabs.SelectedItem is not TabViewItem tab) return;
        currentGroup = (int)tab.Tag;
        FilterSongs();
    }
    private async void AddLibraryTab(TabView sender, object args) => await Run(() => GroupEditor(null));
    private async Task GroupEditor(LibraryGroup? group)
    {
        var name = new TextBox { Header = "分组名称", Text = group?.Name ?? "", MaxLength = 40, PlaceholderText = "例如：练习曲、常用曲目" };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Width = 420, Spacing = 12 }; content.Children.Add(name); content.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = group is null ? "新建分组" : "重命名分组", Content = content, PrimaryButtonText = "保存", CloseButtonText = "取消" };
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            var defer = e.GetDeferral(); dialog.IsPrimaryButtonEnabled = false;
            try
            {
                var result = await backend.Call("group_save", new { id = group?.Id ?? 0, name = name.Text });
                currentGroup = result.GetProperty("id").GetInt32();
            }
            catch (Exception ex) { e.Cancel = true; error.Text = ex.Message; }
            finally { dialog.IsPrimaryButtonEnabled = true; defer.Complete(); }
        };
        if (await ShowDialog(dialog) == ContentDialogResult.Primary) await RefreshLibrary();
    }
    private void SortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        savedSort = Math.Max(0, SortBox.SelectedIndex); FilterSongs(); if (!selfTest) SaveSettings();
    }
    private void RefreshAlphabet()
    {
        AlphabetRail.Visibility = SortBox.SelectedIndex >= 4 ? Visibility.Visible : Visibility.Collapsed;
        AlphabetButtons.Children.Clear();
        if (AlphabetRail.Visibility != Visibility.Visible) return;
        foreach (char letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ#")
        {
            string index = letter.ToString();
            var first = visibleSongs.FirstOrDefault(s => s.NameInitial == index);
            var button = new Button { Content = index, Width = 24, Height = AlphabetHeight(AlphabetRail.ActualHeight), MinHeight = 0, MinWidth = 0, Padding = new Thickness(0), FontSize = 11, IsEnabled = first is not null };
            ToolTipService.SetToolTip(button, first is null ? $"没有 {index} 开头的曲目" : $"跳转到 {index}");
            button.Click += (_, _) => { if (first is not null) SongsList.ScrollIntoView(first, ScrollIntoViewAlignment.Leading); };
            AlphabetButtons.Children.Add(button);
        }
    }
    private static double AlphabetHeight(double height) => height > 0 ? Math.Clamp((height - 2) / 27, 12, 20) : 20;
    private void AlphabetSizeChanged(object sender, SizeChangedEventArgs e)
    {
        foreach (var button in AlphabetButtons.Children.OfType<Button>()) button.Height = AlphabetHeight(e.NewSize.Height);
    }
    private void MultiSelectClick(object sender, RoutedEventArgs e)
    {
        multiSelect = MultiSelectButton.IsChecked == true;
        updating = true;
        SongsList.SelectionMode = multiSelect ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;
        SongsList.SelectedIndex = -1;
        if (multiSelect) SongsList.SelectedItems.Clear();
        if (!multiSelect && selected is not null && visibleSongs.Contains(selected)) SongsList.SelectedItem = selected;
        updating = false; UpdateBatchControls();
    }
    private void UpdateBatchControls()
    {
        BatchToolbar.Visibility = multiSelect ? Visibility.Visible : Visibility.Collapsed;
        int count = SongsList.SelectedItems.Count;
        BatchCount.Text = $"已选 {count} 首";
        BatchExportButton.IsEnabled = BatchGroupButton.IsEnabled = BatchFavoriteButton.IsEnabled = BatchUnfavoriteButton.IsEnabled = count > 0;
        BatchRemoveButton.IsEnabled = count > 0 && currentGroup > 0;
        GoButton.IsEnabled = EditButton.IsEnabled = ExportButton.IsEnabled = DeleteButton.IsEnabled = FavoriteButton.IsEnabled = SongGroupsButton.IsEnabled = !multiSelect && selected is not null;
        FavoriteButton.Content = selected?.Favorite == true ? "取消收藏" : "收藏曲目";
    }
    private void SelectAllClick(object sender, RoutedEventArgs e) => SongsList.SelectAll();
    private void ClearSelectionClick(object sender, RoutedEventArgs e) => SongsList.SelectedItems.Clear();
    private int[] BatchIds() => SongsList.SelectedItems.OfType<SongItem>().Select(s => s.Id).ToArray();
    private async void BatchFavoriteClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        await backend.Call("library_batch", new { ids = BatchIds(), favorite = ((Button)sender).Tag.ToString() == "on" });
        await RefreshLibrary();
    });
    private async void BatchRemoveClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        await backend.Call("library_batch", new { ids = BatchIds(), group_ids = new[] { currentGroup }, remove = true });
        await RefreshLibrary(); StatusText.Text = "已移出当前分组，曲目仍在全部曲目中";
    });
    private async void BatchGroupClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        int[] ids = BatchIds();
        var choice = await ChooseGroups("加入分组", [], false);
        if (choice is null) return;
        await backend.Call("library_batch", new { ids, group_ids = choice.Groups, favorite = choice.Favorite ? (bool?)true : null });
        await RefreshLibrary();
    });
    private async void FavoriteClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (selected is null) return;
        await backend.Call("library_batch", new { ids = new[] { selected.Id }, favorite = !selected.Favorite });
        await RefreshLibrary();
    });
    private async void SongGroupsClick(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (selected is null) return;
        int id = selected.Id;
        var choice = await ChooseGroups("设置曲目分组", selected.GroupIds, selected.Favorite);
        if (choice is null) return;
        await backend.Call("library_batch", new { ids = new[] { id }, group_ids = choice.Groups, favorite = choice.Favorite, replace_groups = true });
        await RefreshLibrary();
    });
    private async Task<GroupChoice?> ChooseGroups(string title, int[]? initial = null, bool favorite = false)
    {
        initial ??= currentGroup > 0 ? [currentGroup] : [1];
        var content = new StackPanel { Width = 440, Spacing = 14 };
        content.Children.Add(new TextBlock { Text = "一首曲目可以属于多个分组。勾选目标分组；也可以直接创建新分组。", TextWrapping = TextWrapping.Wrap });
        var checks = new List<CheckBox>();
        var list = new StackPanel { Spacing = 4 };
        foreach (var group in libraryGroups)
        {
            var check = new CheckBox { Content = group.Name, Tag = group.Id, IsChecked = initial.Contains(group.Id) };
            checks.Add(check); list.Children.Add(check);
        }
        content.Children.Add(new ScrollViewer { MaxHeight = 200, Content = list });
        var favoriteCheck = new CheckBox { Content = "同时加入收藏", IsChecked = favorite || (initial.Length > 0 && currentGroup == -1) };
        content.Children.Add(favoriteCheck);
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBox { PlaceholderText = "新分组名称", MaxLength = 40 };
        var create = new Button { Content = "新建" }; Grid.SetColumn(create, 1); row.Children.Add(name); row.Children.Add(create); content.Children.Add(row);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; content.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = content, PrimaryButtonText = "确定", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        TaskCompletionSource? creation = null;
        create.Click += async (_, _) =>
        {
            creation = new TaskCompletionSource();
            string submittedName = name.Text.Trim();
            create.IsEnabled = name.IsEnabled = dialog.IsPrimaryButtonEnabled = false;
            try
            {
                var result = await backend.Call("group_save", new { name = submittedName });
                int id = result.GetProperty("id").GetInt32();
                var check = new CheckBox { Content = submittedName, Tag = id, IsChecked = true };
                list.Children.Add(check); checks.Add(check); name.Text = ""; error.Text = "";
            }
            catch (Exception ex) { error.Text = ex.Message; }
            finally { create.IsEnabled = name.IsEnabled = dialog.IsPrimaryButtonEnabled = true; creation.SetResult(); }
        };
        var result = await ShowTestableDialog(dialog, content, "group-picker-preview.png");
        if (creation is not null) await creation.Task;
        if (result != ContentDialogResult.Primary) { await RefreshLibrary(); return null; }
        return new GroupChoice(checks.Where(c => c.IsChecked == true).Select(c => (int)c.Tag).ToArray(), favoriteCheck.IsChecked == true);
    }
    private void ShowImportedGroup(GroupChoice choice)
    {
        currentGroup = choice.Groups.FirstOrDefault();
        if (currentGroup == 0 && choice.Favorite) currentGroup = -1;
    }
}
