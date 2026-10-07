using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace Dubbing.App.Controls;

// Render decoded frames through Avalonia, without a platform video window/HWND.
// The player must be stopped/disposed before disposing this view.
public sealed class VideoFrameView : Image, IDisposable
{
    private readonly object gate = new();
    private IntPtr allocation, pixels;
    private byte[] latest = [];
    private int width, height, stride, queued;
    private uint sourceWidth, sourceHeight;
    private bool disposed;
    private WriteableBitmap? bitmap;
    public Image? Mirror { get; set; }
    public long PresentedFrames { get; private set; }

    public VideoFrameView() { Stretch = Stretch.Uniform; }

    public void Attach(MediaPlayer player)
    {
        player.SetVideoCallbacks(Lock, Unlock, Display);
        player.SetVideoFormatCallbacks(Format, Cleanup);
    }
    public void ConfigureSource(uint videoWidth, uint videoHeight)
    { lock (gate) { sourceWidth = videoWidth; sourceHeight = videoHeight; } }

    private uint Format(ref IntPtr opaque, IntPtr chroma, ref uint videoWidth, ref uint videoHeight, ref uint pitch, ref uint lines)
    {
        lock (gate)
        {
            FreeBuffer();
            if (disposed || videoWidth == 0 || videoHeight == 0) return 0;
            // H.264 decoder dimensions can include padding/cropping. Use parsed display dimensions.
            if (sourceWidth > 0 && sourceHeight > 0) { videoWidth = sourceWidth; videoHeight = sourceHeight; }
            double scale = Math.Min(1, 1280d / Math.Max(videoWidth, videoHeight));
            width = Math.Max(1, (int)(videoWidth * scale)); height = Math.Max(1, (int)(videoHeight * scale));
            stride = (width * 4 + 31) & ~31;
            int allocatedLines = (height + 31) & ~31;
            allocation = Marshal.AllocHGlobal(checked(stride * allocatedLines + 31));
            pixels = (IntPtr)((allocation.ToInt64() + 31) & ~31L);
            latest = new byte[stride * height];
            Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
            videoWidth = (uint)width; videoHeight = (uint)height; pitch = (uint)stride; lines = (uint)height;
            return 1;
        }
    }

    private IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        Monitor.Enter(gate);
        Marshal.WriteIntPtr(planes, pixels);
        return IntPtr.Zero;
    }
    private void Unlock(IntPtr opaque, IntPtr picture, IntPtr planes) => Monitor.Exit(gate);
    private void Display(IntPtr opaque, IntPtr picture)
    {
        lock (gate)
        {
            if (disposed || pixels == IntPtr.Zero) return;
            Marshal.Copy(pixels, latest, 0, latest.Length);
        }
        if (Interlocked.Exchange(ref queued, 1) == 0) Dispatcher.UIThread.Post(Present, DispatcherPriority.Render);
    }

    private void Present()
    {
        lock (gate)
        {
            Interlocked.Exchange(ref queued, 0);
            if (disposed || latest.Length == 0) return;
            if (bitmap is null || bitmap.PixelSize != new PixelSize(width, height))
            {
                Source = null; bitmap?.Dispose();
                bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                Source = bitmap; if (Mirror is not null) Mirror.Source = bitmap;
            }
            using (var frame = bitmap.Lock())
                for (int row = 0; row < height; row++)
                    Marshal.Copy(latest, row * stride, frame.Address + row * frame.RowBytes, width * 4);
            PresentedFrames++;
            InvalidateVisual(); Mirror?.InvalidateVisual();
        }
    }
    private void Cleanup(ref IntPtr opaque) { lock (gate) FreeBuffer(); }
    private void FreeBuffer()
    {
        if (allocation != IntPtr.Zero) Marshal.FreeHGlobal(allocation);
        allocation = pixels = IntPtr.Zero;
    }
    public void ClearFrame() { lock (gate) { latest = []; Source = null; if (Mirror is not null) Mirror.Source = null; bitmap?.Dispose(); bitmap = null; } }
    public void Dispose()
    {
        lock (gate) { disposed = true; FreeBuffer(); latest = []; }
        ClearFrame();
    }
}
