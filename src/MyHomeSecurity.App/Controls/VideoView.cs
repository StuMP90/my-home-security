using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MyHomeSecurity.Core.Cameras;

namespace MyHomeSecurity.App.Controls;

/// <summary>
/// Draws a <see cref="CameraStream"/>'s frames. Frames arrive on a background thread; at most one
/// redraw is queued at a time, and each copies only the newest frame straight into the bitmap, so
/// a busy UI drops frames instead of falling behind. Nothing is drawn while the control isn't shown.
/// Until the source's first frame, <see cref="Placeholder"/> (the last picture) is shown instead.
/// </summary>
public sealed class VideoView : Control
{
    public static readonly StyledProperty<CameraStream?> SourceProperty =
        AvaloniaProperty.Register<VideoView, CameraStream?>(nameof(Source));

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<VideoView, Stretch>(nameof(Stretch), Stretch.Uniform);

    public static readonly StyledProperty<IImage?> PlaceholderProperty =
        AvaloniaProperty.Register<VideoView, IImage?>(nameof(Placeholder));

    private CameraStream? _subscribed;
    private WriteableBitmap? _bitmap;
    private long _lastFrame;
    private int _pending;
    private bool _attached;

    static VideoView()
    {
        AffectsRender<VideoView>(StretchProperty, PlaceholderProperty);
        ClipToBoundsProperty.OverrideDefaultValue<VideoView>(true);
    }

    public CameraStream? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    /// <summary>Shown while there's no frame from <see cref="Source"/>, e.g. the camera's last picture.</summary>
    public IImage? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>The stream's newest frame as a bitmap, or null if none has arrived.</summary>
    public static Bitmap? LatestFrameOf(CameraStream stream)
    {
        if (stream.GetLatestFrame() is not { } pixels)
            return null;
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, handle.AddrOfPinnedObject(),
                new PixelSize(stream.Width, stream.Height), new Vector(96, 96), stream.Stride);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>True once a frame has been drawn from the current source.</summary>
    public bool HasFrame => _bitmap is not null && _lastFrame > 0;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
            Resubscribe();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Resubscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Resubscribe();
    }

    private void Resubscribe()
    {
        var wanted = _attached ? Source : null;
        if (ReferenceEquals(wanted, _subscribed))
            return;
        if (_subscribed is not null)
            _subscribed.FrameArrived -= OnFrameArrived;
        _subscribed = wanted;
        _lastFrame = 0;
        if (_bitmap is not null && (wanted is null || _bitmap.PixelSize != new PixelSize(wanted.Width, wanted.Height)))
        {
            _bitmap.Dispose();
            _bitmap = null;
        }
        if (wanted is not null)
        {
            wanted.FrameArrived += OnFrameArrived;
            if (wanted.FrameNumber > 0)
                OnFrameArrived();
        }
        InvalidateVisual();
    }

    private void OnFrameArrived()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 0)
            Dispatcher.UIThread.Post(UpdateFrame, DispatcherPriority.Render);
    }

    private void UpdateFrame()
    {
        Interlocked.Exchange(ref _pending, 0);
        var stream = _subscribed;
        if (stream is null || !IsEffectivelyVisible)
            return;
        _bitmap ??= new WriteableBitmap(new PixelSize(stream.Width, stream.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        bool copied;
        using (var buffer = _bitmap.Lock())
            copied = stream.CopyLatestFrame(buffer.Address, buffer.RowBytes, ref _lastFrame);
        if (copied)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Black, bounds);
        if (_bitmap is not null && _lastFrame > 0)
            Draw(context, _bitmap, bounds);
        else if (Placeholder is { } placeholder)
            Draw(context, placeholder, bounds);
    }

    private void Draw(DrawingContext context, IImage image, Rect bounds)
    {
        var source = new Rect(image.Size);
        if (source.Width <= 0 || source.Height <= 0)
            return;
        var scale = Stretch.CalculateScaling(bounds.Size, source.Size);
        var size = source.Size * scale;
        var dest = new Rect(bounds.Center.X - (size.Width / 2), bounds.Center.Y - (size.Height / 2), size.Width, size.Height);
        // UniformToFill: crop the source so the drawn area stays inside the control.
        var clipped = dest.Intersect(bounds);
        var src = new Rect(
            (clipped.X - dest.X) / scale.X, (clipped.Y - dest.Y) / scale.Y,
            clipped.Width / scale.X, clipped.Height / scale.Y);
        context.DrawImage(image, src, clipped);
    }
}
