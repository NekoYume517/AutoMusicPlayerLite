namespace AutoMusicPlayer;

internal static class DistributionMode
{
    internal static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.mode"));
    internal static bool IsMsix
    {
        get
        {
            try { return Windows.ApplicationModel.Package.Current.Id.Name.Length > 0; }
            catch (System.Runtime.InteropServices.COMException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }
    internal static string Executable => Environment.ProcessPath ?? throw new IOException("找不到当前程序文件。");
}
