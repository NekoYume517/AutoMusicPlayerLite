using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using System.Runtime.InteropServices.WindowsRuntime;
namespace AutoMusicPlayer;
internal static class UiSnapshot
{
    internal static async Task Save(FrameworkElement surface, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(surface);
        byte[] pixels = (await bitmap.GetPixelsAsync()).ToArray();
        if (bitmap.PixelWidth < 1 || bitmap.PixelHeight < 1) throw new Exception("UI snapshot did not render");
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
    }
}
