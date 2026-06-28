using System.Windows.Media.Imaging;
using System.IO;
using KickoutMonitor.Application;

namespace KickoutMonitor.App.Services;

public sealed class WpfPreviewImageLoader : IPreviewImageLoader<BitmapSource>
{
    public Task<BitmapSource?> LoadAsync(
        string networkPath,
        int decodeWidth,
        CancellationToken cancellationToken)
    {
        return Task.Run<BitmapSource?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(
                    networkPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    128 * 1024,
                    FileOptions.SequentialScan);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = decodeWidth;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }, cancellationToken);
    }
}
