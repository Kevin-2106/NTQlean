using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace NTQlean.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Hidden diagnostic: NTQlean.App.exe --grab-test <video> <out.png>
        // Renders one frame with the OS decoder and exits (no window).
        if (e.Args is [.., "--grab-test", _] or ["--grab-test", _, _])
        {
            var i = Array.IndexOf(e.Args, "--grab-test");
            var video = e.Args[i + 1];
            var output = e.Args.Length > i + 2 ? e.Args[i + 2] : "grab-test.png";
            var frame = VideoFrameGrabber.GrabAsync(video).GetAwaiter().GetResult();
            if (frame is null)
            {
                Console.Error.WriteLine("GRAB-FAIL");
                Shutdown(2);
                return;
            }

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(frame));
            using var fs = new FileStream(output, FileMode.Create);
            enc.Save(fs);
            Console.WriteLine($"GRAB-OK {frame.PixelWidth}x{frame.PixelHeight}");
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
    }
}
