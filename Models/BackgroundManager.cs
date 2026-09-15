// Helpers/BackgroundManager.cs
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI;
using singC.Helpers;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace singC.Models
{
    public static class BackgroundManager
    {
        public static ImageBrush? CurrentBackgroundBrush { get; private set; }
        public static SolidColorBrush? RecommendedForegroundBrush { get; private set; }
        public static string? ImagePath { get; private set; }

        public static event Action? BackgroundChanged;

        public static async Task InitializeAsync()
        {
            try
            {
                string? imagePath = AppSettings.Get(AppSettings.PathKey.BackgroundImagePathKey);
                if (!string.IsNullOrWhiteSpace(imagePath))
                    await SetBackgroundFromPathAsync(imagePath);
            }
            catch (Exception ex)
            {
                // A stale or unreadable background is not a reason to abort startup.
                CurrentBackgroundBrush = null;
                RecommendedForegroundBrush = null;
                ImagePath = null;
                Debug.WriteLine($"读取保存的背景失败: {ex.Message}");
            }
        }

        // 从文件路径加载背景
        public static async Task SetBackgroundFromPathAsync(string path)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var bitmap = new BitmapImage();
                using (var stream = await file.OpenReadAsync())
                {
                    await bitmap.SetSourceAsync(stream);
                }
                CurrentBackgroundBrush = new ImageBrush
                {
                    ImageSource = bitmap,
                    Stretch = Stretch.UniformToFill
                };
                RecommendedForegroundBrush = new SolidColorBrush(await GetReadableForegroundColorAsync(file));
                ImagePath = path;
            }
            catch
            {
                // 如果文件无效，清空背景
                CurrentBackgroundBrush = null;
                RecommendedForegroundBrush = null;
                ImagePath = null;
                throw;
            }
            BackgroundChanged?.Invoke();
        }

        private static async Task<Windows.UI.Color> GetReadableForegroundColorAsync(StorageFile file)
        {
            using IRandomAccessStream stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            uint width = Math.Max(1, Math.Min(decoder.PixelWidth, 64));
            uint height = Math.Max(1, Math.Min(decoder.PixelHeight, 64));

            var transform = new BitmapTransform
            {
                ScaledWidth = width,
                ScaledHeight = height,
                InterpolationMode = BitmapInterpolationMode.Linear
            };

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            byte[] pixels = pixelData.DetachPixelData();
            if (pixels.Length == 0) return Colors.White;

            double totalLuminance = 0;
            int pixelCount = pixels.Length / 4;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte blue = pixels[i];
                byte green = pixels[i + 1];
                byte red = pixels[i + 2];
                totalLuminance += (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
            }

            double averageLuminance = totalLuminance / pixelCount;
            return averageLuminance < 140 ? Colors.White : Colors.Black;
        }


        public static void ClearBackground()
        {
            if(CurrentBackgroundBrush != null)
            {
                CurrentBackgroundBrush = null;
                RecommendedForegroundBrush = null;
                ImagePath = null;
                BackgroundChanged?.Invoke();
            }
        }
    }
}
