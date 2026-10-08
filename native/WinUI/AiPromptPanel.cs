using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace AutoMusicPlayer;

internal sealed class AiPromptPanel : UserControl
{
    internal readonly Button CopyButton = new() { Content = "复制提示词", Padding = new Thickness(12, 6, 12, 6), VerticalAlignment = VerticalAlignment.Center };
    internal readonly Expander Details = new() { Header = "查看完整提示词", HorizontalAlignment = HorizontalAlignment.Stretch };
    internal readonly TextBox PromptText;
    internal string Text { get; }

    internal AiPromptPanel(string text, Action<string> copy, Style cardStyle)
    {
        Text = text;
        var border = new Border { Style = cardStyle, CornerRadius = new CornerRadius(8), Padding = new Thickness(12) };
        var layout = new StackPanel { Spacing = 8 };
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "用 AI 识谱", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(CopyButton, 1); header.Children.Add(CopyButton); layout.Children.Add(header);
        layout.Children.Add(new TextBlock { Text = "把提示词与乐谱图片一起发给 AI，再把返回的简谱粘贴到下方。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 });
        PromptText = new TextBox { AcceptsReturn = true, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            Height = 160, FontSize = 12, IsSpellCheckEnabled = false, Text = text };
        Details.Content = PromptText; layout.Children.Add(Details);
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed };
        layout.Children.Add(feedback);
        CopyButton.Click += (_, _) =>
        {
            try { copy(Text); CopyButton.Content = "已复制"; feedback.Text = "已复制完整提示词，可粘贴到 AI 对话中。"; }
            catch (Exception) { feedback.Text = "复制失败，请展开提示词，选中文字后手动复制。"; }
            feedback.Visibility = Visibility.Visible;
        };
        border.Child = layout; Content = border;
    }
}
