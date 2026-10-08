using System.Text.Json;

namespace AutoMusicPlayer;

public sealed partial class MainWindow
{
    private async Task CheckDecimalLogs(List<string> checks)
    {
        string directory = Path.Combine(backend.DataDirectory, "play_logs");
        Directory.CreateDirectory(directory);
        foreach (string value in new[] { "112.5", "300.0", "100" })
        {
            string filename = "decimal-log-" + value + ".jsonl", name = "小数 BPM 自检 " + value;
            string content = "{\"type\":\"session_start\",\"score_name\":\"" + name + "\",\"started_at\":\"2026-10-09T00:00:00\",\"bpm\":" + value + "}\n" +
                "{\"type\":\"note\",\"index\":0,\"notes\":[\"mid_1\"],\"keys\":[\"Z\"],\"success\":true}\n";
            File.WriteAllText(Path.Combine(directory, filename), content);
            await RefreshLogs();
            var item = ((IEnumerable<LogItem>)LogList.ItemsSource).Single(x => x.File == filename);
            double expected = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            if (item.Bpm != expected || !item.Display.Contains(expected.ToString("0.##"))) throw new Exception("Decimal BPM log failed to display");
            LogList.SelectedItem = item;
            for (int i = 0; i < 30 && !LogDetail.Text.Contains(name); i++) await Task.Delay(50);
            if (!LogDetail.Text.Contains(name) || !LogDetail.Text.Contains("session_start")) throw new Exception("Decimal log selection did not load details");
            string json = Path.Combine(backend.DataDirectory, filename + ".export.jsonl"), csv = Path.ChangeExtension(json, ".csv");
            await backend.Call("log_export", new { file = filename, path = json });
            await backend.Call("log_export", new { file = filename, path = csv });
            using var header = JsonDocument.Parse(File.ReadLines(json).First());
            if (header.RootElement.GetProperty("bpm").GetDouble() != expected || !File.ReadAllText(csv).Contains("mid_1")) throw new Exception("Decimal log export changed its data");
        }
        checks.Add("historical integer, integral float and fractional BPM logs display, select and export to JSONL/CSV without rewriting originals");
    }
}
