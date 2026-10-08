using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
namespace AutoMusicPlayer;

public sealed partial class MainWindow
{
    private string? copiedPromptInSelfTest;
    private static string NormalizeLines(string value) => value.Replace("\r\n", "\n").Replace("\r", "\n");

    private async Task CheckPromptDialog(ContentDialog dialog, ScrollViewer viewport, AiPromptPanel panel)
    {
        await Task.Delay(120);
        if (panel.ActualWidth <= 0 || !panel.PromptText.IsReadOnly || NormalizeLines(panel.PromptText.Text) != NormalizeLines(panel.Text))
            throw new Exception($"AI prompt render: width={panel.ActualWidth}, readonly={panel.PromptText.IsReadOnly}, renderedLength={panel.PromptText.Text.Length}, originalLength={panel.Text.Length}, renderedCR={panel.PromptText.Text.Count(c => c == '\r')}, originalLF={panel.Text.Count(c => c == '\n')}");
        ((IInvokeProvider)new ButtonAutomationPeer(panel.CopyButton).GetPattern(PatternInterface.Invoke)).Invoke();
        await Task.Delay(60);
        if (copiedPromptInSelfTest != panel.Text || panel.CopyButton.Content?.ToString() != "已复制")
            throw new Exception("Copy button did not send the complete prompt to the clipboard action");
        await UiSnapshot.Save(viewport, Path.Combine(Path.GetDirectoryName(selfTestFile!)!, "new-score-ai-prompt.png"));
        panel.Details.IsExpanded = true; await Task.Delay(100);
        panel.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
        await Task.Delay(100);
        var point = panel.PromptText.TransformToVisual(viewport).TransformPoint(new Windows.Foundation.Point());
        if (panel.PromptText.ActualWidth < 100 || point.X < -1 || point.X + panel.PromptText.ActualWidth > viewport.ActualWidth + 1 ||
            viewport.ActualHeight > Math.Max(240, Root.ActualHeight - 260) + 1 || !dialog.IsPrimaryButtonEnabled)
            throw new Exception("Expanded prompt is clipped or editor actions are unavailable");
        await UiSnapshot.Save(viewport, Path.Combine(Path.GetDirectoryName(selfTestFile!)!, "new-score-ai-prompt-expanded.png"));
    }
}
