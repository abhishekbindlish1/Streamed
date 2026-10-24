using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace StreamedTv.Theme
{
    /// <summary>Helpers for applying the project's "fake glass" surface treatment to NUI Views.
    /// NUI lacks backdrop blur; against the dark page background, semi-transparent fill + thin top
    /// highlight + soft shadow reads convincingly enough.
    ///
    /// IMPORTANT: in Tizen 9.0 NUI, View.CornerRadius is Vector4 (not float) and Shadow has a
    /// parameterless constructor. Calling these against the wrong reference assembly (we used to
    /// target tizen80) produces MissingMethodException at runtime on the device.</summary>
    public static class Glass
    {
        public static void Apply(View view, float cornerRadius)
        {
            if (view == null) return;
            view.BackgroundColor = Tokens.SurfaceGlass;
            // CornerRadius takes a Vector4 (top-left, top-right, bottom-right, bottom-left) since Tizen 9.
            view.CornerRadius = new Vector4(cornerRadius, cornerRadius, cornerRadius, cornerRadius);
            view.CornerRadiusPolicy = VisualTransformPolicyType.Absolute;

            // Soft shadow. Shadow(blurRadius, color, offset, extents) is the canonical Tizen 9 ctor.
            view.BoxShadow = new Shadow(
                blurRadius: 18f,
                color: new Color(0f, 0f, 0f, 0.45f),
                offset: new Vector2(0f, 6f),
                extents: new Vector2(0f, 0f));
        }

        /// <summary>Apply rounded corners + shadow + a 1px borderline. Tizen 9 only.</summary>
        public static void ApplyWithBorder(View view, float cornerRadius, Color borderColor, float borderWidth = 1f)
        {
            Apply(view, cornerRadius);
            view.BorderlineColor = borderColor;
            view.BorderlineWidth = borderWidth;
            view.BorderlineOffset = -1f; // pull the borderline inside the corner radius
        }

        /// <summary>Adds a 1px tall highlight strip across the top of the view (visual top-edge shine).</summary>
        public static View AddTopHighlight(View parent)
        {
            View highlight = new View
            {
                BackgroundColor = Tokens.SurfaceHighlight,
                WidthResizePolicy = ResizePolicyType.FillToParent,
                SizeHeight = 1f,
                ParentOrigin = Tizen.NUI.ParentOrigin.TopCenter,
                PivotPoint = Tizen.NUI.PivotPoint.TopCenter,
            };
            parent.Add(highlight);
            return highlight;
        }
    }
}
