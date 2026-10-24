using global::Android.Content;
using global::Android.Runtime;
using global::Android.Views;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using Microsoft.Maui.Graphics;

namespace StreamedTv.Platforms.Android;

// Shell can initially measure a page without a finite constraint. A star-sized
// root Grid must not turn that into Android's 0x00ffffff measured-size sentinel.
// Bound only page roots, using the real window, so scroll contents remain free
// to exceed their viewport and rotation/split-screen use the current dimensions.
public sealed class BoundedPageHandler : PageHandler
{
    private Size Viewport()
    {
        var decor = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window?.DecorView;
        var metrics = Context.Resources?.DisplayMetrics;
        var density = metrics?.Density ?? 1;
        return new Size((decor?.Width > 0 ? decor.Width : metrics?.WidthPixels ?? 0) / density,
            (decor?.Height > 0 ? decor.Height : metrics?.HeightPixels ?? 0) / density);
    }

    private static double Finite(double value, double limit) =>
        limit > 0 && (!double.IsFinite(value) || value > limit) ? limit : value;

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        var viewport = Viewport();
        var desired = base.GetDesiredSize(Finite(widthConstraint, viewport.Width), Finite(heightConstraint, viewport.Height));
        return new Size(Finite(desired.Width, viewport.Width), Finite(desired.Height, viewport.Height));
    }

    public override void PlatformArrange(Rect frame)
    {
        var viewport = Viewport();
        base.PlatformArrange(new Rect(frame.X, frame.Y, Finite(frame.Width, viewport.Width), Finite(frame.Height, viewport.Height)));
    }

    protected override ContentViewGroup CreatePlatformView()
    {
        var view = new BoundedPageView(Context) { CrossPlatformLayout = VirtualView };
        view.SetClipChildren(false);
        return view;
    }
}

public sealed class BoundedPageView : ContentViewGroup
{
    public BoundedPageView(Context context) : base(context) { }
    public BoundedPageView(IntPtr reference, JniHandleOwnership transfer) : base(reference, transfer) { }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        var decor = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window?.DecorView;
        var width = decor?.Width > 0 ? decor.Width : Resources?.DisplayMetrics?.WidthPixels ?? 0;
        var height = decor?.Height > 0 ? decor.Height : Resources?.DisplayMetrics?.HeightPixels ?? 0;
        base.OnMeasure(Bound(widthMeasureSpec, width), Bound(heightMeasureSpec, height));
        // A page's virtual measure can still report an unconstrained desired
        // size. Keep that value out of Shell's measured native dimensions.
        if (width > 0 && height > 0 && (MeasuredWidth > width || MeasuredHeight > height))
        {
            global::Android.Util.Log.Info("StreamedTv", $"Constrained page layout {MeasuredWidth}x{MeasuredHeight} to window {width}x{height}");
            SetMeasuredDimension(Math.Min(MeasuredWidth, width), Math.Min(MeasuredHeight, height));
        }
    }

    protected override void OnLayout(bool changed, int left, int top, int right, int bottom)
    {
        var decor = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window?.DecorView;
        var width = decor?.Width > 0 ? decor.Width : Resources?.DisplayMetrics?.WidthPixels ?? 0;
        var height = decor?.Height > 0 ? decor.Height : Resources?.DisplayMetrics?.HeightPixels ?? 0;
        base.OnLayout(changed, left, top,
            width > 0 ? left + Math.Min(right - left, width) : right,
            height > 0 ? top + Math.Min(bottom - top, height) : bottom);
    }

    private static int Bound(int spec, int viewport)
    {
        var mode = MeasureSpec.GetMode(spec);
        if (viewport > 0 && (mode == MeasureSpecMode.Unspecified || MeasureSpec.GetSize(spec) > viewport))
            return MeasureSpec.MakeMeasureSpec(viewport, mode == MeasureSpecMode.Unspecified ? MeasureSpecMode.Exactly : mode);
        return spec;
    }
}
