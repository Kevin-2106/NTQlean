using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NTQlean.App;

public static class VideoFrameGrabber
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Grab one preview frame. Serialized: rapid selection changes must not stack decoder threads.</summary>
    public static Task<BitmapSource?> GrabAsync(string path) => Task.Run(async () =>
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        await Gate.WaitAsync(TimeSpan.FromSeconds(8));
        try
        {
            BitmapSource? result = null;
            var thread = new Thread(() => result = GrabCore(path));
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(7)); // GrabCore self-limits to ~5s; join is the outer guard
            return result;
        }
        catch
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    });

    private static BitmapSource? GrabCore(string path)
    {
        MediaPlayer? player = null;
        try
        {
            var opened = false;
            var failed = false;
            player = new System.Windows.Media.MediaPlayer { ScrubbingEnabled = true, Volume = 0 };
            player.MediaOpened += (_, _) => opened = true;
            player.MediaFailed += (_, _) => failed = true;

            player.Open(new Uri(path));
            player.Play();

            var seeked = false;
            var budget = System.Diagnostics.Stopwatch.StartNew();
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (_, _) =>
            {
                if (failed || budget.Elapsed > TimeSpan.FromSeconds(5))
                {
                    frame.Continue = false;
                    return;
                }
                if (!opened) return;
                if (!seeked)
                {
                    seeked = true;
                    // A frame near the start; for very short clips land in the middle.
                    var target = player.NaturalDuration is { HasTimeSpan: true } dur
                                 && dur.TimeSpan < TimeSpan.FromSeconds(4)
                        ? dur.TimeSpan / 2
                        : TimeSpan.FromSeconds(1);
                    player.Position = target;
                    budget.Restart(); // the seek gets its own slice of the budget
                }
                else if (player.Position > TimeSpan.Zero && budget.Elapsed > TimeSpan.FromMilliseconds(400))
                {
                    frame.Continue = false;
                }
            };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame); // pump events until frame ready / timeout
            timer.Stop();

            if (!opened || failed || player.NaturalVideoWidth <= 0) return null;

            var vw = player.NaturalVideoWidth;
            var vh = player.NaturalVideoHeight;
            var scale = Math.Min(1.0, 1024.0 / Math.Max(vw, vh));
            var w = Math.Max(2, (int)(vw * scale));
            var h = Math.Max(2, (int)(vh * scale));
            var visual = new System.Windows.Media.DrawingVisual();
            using (var ctx = visual.RenderOpen())
                ctx.DrawVideo(player, new Rect(0, 0, w, h));
            var bmp = new RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze(); // cross-thread safety
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            player?.Close();
        }
    }
}
