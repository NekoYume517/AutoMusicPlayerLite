using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoMusicPlayer;
public sealed partial class MainWindow
{
    private static List<string> ReportMessages(JsonElement result, string key) =>
        result.TryGetProperty(key, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(value => value.ToString()).ToList() : [];

    private static string[] GroupReportMessages(IEnumerable<string> messages) =>
        messages.Where(message => !string.IsNullOrWhiteSpace(message)).GroupBy(message => message)
            .Select(group => group.Count() == 1 ? group.Key : $"{group.Key}（{group.Count()} 次）").ToArray();

    private async Task ShowOperationReport(string title, string summary, IEnumerable<string> messages)
    {
        if (closing) return;
        if (mini is not null) { mini.ShowMessage(summary); return; }
        var grouped = GroupReportMessages(messages);
        var content = new StackPanel { Width = 460, Spacing = 16 };
        content.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, FontSize = 16 });
        if (grouped.Length > 0)
            content.Children.Add(new Expander { Header = $"查看详情 · {grouped.Length} 类提示", HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new ScrollViewer { MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock { Text = string.Join("\n\n", grouped), TextWrapping = TextWrapping.Wrap, MaxWidth = 420, FontSize = 13 } } });
        await ShowTestableDialog(new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = content, CloseButtonText = "知道了" }, content, "operation-report-preview.png");
    }

    private async Task CheckOperationReports(List<string> checks)
    {
        var messages = Enumerable.Repeat("长音已分段，时长保持不变。", 100).Append("已有曲目保留。").ToArray();
        var grouped = GroupReportMessages(messages);
        if (grouped.Length != 2 || !grouped[0].Contains("100 次")) throw new Exception("Repeated report messages were not grouped");
        await ShowOperationReport("操作完成", "已完成 · 提示已合并，可展开查看详情", messages);
        checks.Add("successful operation report renders with grouped, collapsible and bounded details");
    }
}
