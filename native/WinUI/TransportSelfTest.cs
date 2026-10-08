using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace AutoMusicPlayer;

public sealed partial class MainWindow
{
    private async Task CheckTransportPlacement(List<string> checks)
    {
        foreach (var page in new[] { Nav.MenuItems[0], Nav.MenuItems[1], Nav.MenuItems[2], Nav.SettingsItem })
        {
            Nav.SelectedItem = page; await Task.Delay(100);
            var entry = MiniButton.TransformToVisual(PageHeader).TransformPoint(new Windows.Foundation.Point());
            if (MiniButton.Visibility != Visibility.Visible || MiniButton.ActualWidth <= 0 || entry.X < PageHeader.ActualWidth / 2 ||
                entry.Y < 0 || entry.X + MiniButton.ActualWidth > PageHeader.ActualWidth + 1 || entry.Y + MiniButton.ActualHeight > PageHeader.ActualHeight + 1)
                throw new Exception("mini entry is missing or clipped on a navigation page");
        }
        checks.Add("mini entry remains in the fixed upper-right header on all four pages");
        Nav.SelectedItem = Nav.MenuItems[0]; await Task.Delay(150);
        foreach (var control in new FrameworkElement[] { PlayButton, PauseButton, PreviewButton, ResetButton, SeekSlider })
        {
            DependencyObject? current = control;
            while (current is not null && current != LibraryTransport) current = VisualTreeHelper.GetParent(current);
            var point = control.TransformToVisual(LibraryDetail).TransformPoint(new Windows.Foundation.Point());
            if (current != LibraryTransport || control.ActualHeight <= 0 || point.Y < 0 || point.Y + control.ActualHeight > LibraryDetail.ActualHeight + 1)
                throw new Exception("library transport is missing or clipped: " + control.Name);
        }
        if (!PlayButton.IsEnabled || !PreviewButton.IsEnabled || !ResetButton.IsEnabled || PauseButton.IsEnabled)
            throw new Exception("library transport enablement does not match the idle selected song");
        checks.Add("library right detail contains visible play/pause, preview, reset and progress controls");
        if (Root.FindName("SyncButton") is Button sync)
        {
            var a = MiniButton.TransformToVisual(PageHeader).TransformPoint(new Windows.Foundation.Point());
            var b = sync.TransformToVisual(PageHeader).TransformPoint(new Windows.Foundation.Point());
            if (sync.Visibility != Visibility.Visible || b.X < a.X + MiniButton.ActualWidth)
                throw new Exception("mini entry is not left of the private synchronization button");
            checks.Add("private library places the mini entry immediately left of synchronization");
        }
        await UiSnapshot.Save(Root, Path.Combine(Path.GetDirectoryName(selfTestFile!)!, "library-player-preview.png"));
    }
}
