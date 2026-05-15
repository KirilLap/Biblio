using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BibClient
{
    public static class ScreenshotStreamer
    {
        private static CancellationTokenSource? _cts;
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
        private static ImageCodecInfo? _jpegCodec;

        public static void Start(string serverBase, string pcNumber)
        {
            Stop();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(async () =>
            {
                Logger.Info($"📸 Трансляция скриншотов запущена ({pcNumber})");
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var bytes = CaptureScreen();
                        var content = new ByteArrayContent(bytes);
                        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                        var url = $"{serverBase}/api/screenshot/upload?pc={Uri.EscapeDataString(pcNumber)}";
                        await _http.PostAsync(url, content, token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { Logger.Warn($"Ошибка скриншота: {ex.Message}"); }
                    try { await Task.Delay(500, token); }
                    catch (OperationCanceledException) { break; }
                }
                Logger.Info("📸 Трансляция скриншотов остановлена");
            }, CancellationToken.None);
        }

        public static void Stop()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        private static byte[] CaptureScreen()
        {
            var bounds = Screen.PrimaryScreen!.Bounds;
            using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            using var ms = new MemoryStream();
            var codec = _jpegCodec ??= GetJpegCodec();
            var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, 55L);
            bmp.Save(ms, codec, ep);
            return ms.ToArray();
        }

        private static ImageCodecInfo GetJpegCodec()
        {
            foreach (var c in ImageCodecInfo.GetImageEncoders())
                if (c.MimeType == "image/jpeg") return c;
            throw new InvalidOperationException("JPEG encoder not found");
        }
    }
}
