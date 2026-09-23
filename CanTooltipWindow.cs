using System.Runtime.InteropServices;
using Gtk;

namespace CanLogger;

/// <summary>Positions CAN tooltips relative to their parent, including on Wayland.</summary>
internal sealed class CanTooltipWindow : Window
{
    // GtkSharp does not expose this GTK 3.24 API. Use the native positioning API
    // rather than Gtk.Window.Move: Wayland does not support global window positions.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MoveToRectDelegate(IntPtr window, ref Gdk.Rectangle rect,
        Gdk.Gravity rectAnchor, Gdk.Gravity windowAnchor, Gdk.AnchorHints hints, int dx, int dy);

    private static readonly MoveToRectDelegate MoveToRect =
        Marshal.GetDelegateForFunctionPointer<MoveToRectDelegate>(NativeLibrary.GetExport(
            NativeLibrary.Load(OperatingSystem.IsWindows() ? "libgdk-3-0.dll" :
                OperatingSystem.IsMacOS() ? "libgdk-3.0.dylib" : "libgdk-3.so.0"),
            "gdk_window_move_to_rect"));

    private Gdk.Point _anchor;
    private bool _positionQueued;
    private bool _destroyed;

    public CanTooltipWindow() : base(WindowType.Popup)
    {
        TypeHint = Gdk.WindowTypeHint.Tooltip;
        Decorated = false;
        Resizable = false;
        AcceptFocus = false;
        FocusOnMap = false;
        BorderWidth = 8;
        Name = "gtk-tooltip";
        StyleContext.AddClass("tooltip");
        Destroyed += (_, _) => _destroyed = true;
    }

    public void SetContent(Label label, Gdk.Point anchor)
    {
        if (_anchor.Equals(anchor) && Child is Label current &&
            current.Text == label.Text && current.WidthRequest == label.WidthRequest)
        {
            label.Destroy();
            return;
        }
        _anchor = anchor;
        var previous = Child;
        if (previous != null)
        {
            Remove(previous);
            previous.Destroy();
        }
        Add(label);
        label.Show();
        // Older Wayland compositors cannot reposition a mapped popup. Remap a
        // changed visible tooltip after GTK finishes processing this query.
        if (Visible && !_positionQueued)
        {
            _positionQueued = true;
            GLib.Idle.Add(() =>
            {
                _positionQueued = false;
                if (!_destroyed && Visible)
                {
                    Hide();
                    Show();
                }
                return false;
            });
        }
    }

    protected override void OnShown()
    {
        // GTK has selected its default tooltip position by this point. Replace
        // that request before mapping, so a fullscreen popup never opens clipped.
        Realize();
        PositionPopup();
        base.OnShown();
    }

    private void PositionPopup()
    {
        var parent = TransientFor;
        if (parent == null || !IsRealized) return;
        GetPreferredSize(out _, out var size);
        Resize(size.Width, size.Height);
        var state = parent.Window.State;
        bool bounded = (state & (Gdk.WindowState.Maximized | Gdk.WindowState.Fullscreen)) != 0;
        var rect = GetPopupRectangle(_anchor, size.Width, size.Height,
            parent.AllocatedWidth, parent.AllocatedHeight, bounded);
        // Rectangles are parent-relative. The compositor performs the final
        // screen-edge adjustment for windowed mode, without guessed root coordinates.
        MoveToRect(Window.Handle, ref rect, Gdk.Gravity.NorthWest, Gdk.Gravity.NorthWest,
            Gdk.AnchorHints.SlideX | Gdk.AnchorHints.SlideY, 0, 0);
    }

    internal static Gdk.Rectangle GetPopupRectangle(Gdk.Point anchor, int width, int height,
        int parentWidth, int parentHeight, bool bounded)
    {
        int x = anchor.X - width / 2;
        int y = anchor.Y + 24;
        if (bounded)
        {
            x = Math.Clamp(x, 0, Math.Max(0, parentWidth - width));
            if (y + height > parentHeight) y = anchor.Y - height - 8;
            y = Math.Clamp(y, 0, Math.Max(0, parentHeight - height));
        }
        return new Gdk.Rectangle(x, y, 1, 1);
    }
}
