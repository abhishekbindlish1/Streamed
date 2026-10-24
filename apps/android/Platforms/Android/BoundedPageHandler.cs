using global::Android.Content;
using global::Android.Runtime;
using global::Android.Views;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace StreamedTv.Platforms.Android;

// Shell can initially measure a page without a finite constraint. A star-sized
// root Grid must not turn that into Android's 0x00ffffff measured-size sentinel.
// Bound only page roots, using the real window, so scroll contents remain free
// to exceed their viewport and rotation/split-screen use the current dimensions.
public sealed class BoundedPageHandler : PageHandler
{
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

    private static int Bound(int spec, int viewport)
    {
        var mode = MeasureSpec.GetMode(spec);
        if (viewport > 0 && (mode == MeasureSpecMode.Unspecified || MeasureSpec.GetSize(spec) > viewport))
            return MeasureSpec.MakeMeasureSpec(viewport, mode == MeasureSpecMode.Unspecified ? MeasureSpecMode.Exactly : mode);
        return spec;
    }
}
