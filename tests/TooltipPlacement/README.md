# Tooltip placement regression check

Run from the project root on a Linux GTK desktop:

```bash
dotnet run --project tests/TooltipPlacement/TooltipPlacement.csproj
```

Install .NET 10 SDK 10.0.401 or a newer stable .NET 10 SDK, as selected by
`global.json`. No runtime roll-forward override is needed.

The check opens temporary CAN Logger windows in windowed, maximized and fullscreen
modes. It uses the real tooltip handlers for both views at left, middle and right
positions, including updates while the tooltip remains open. It verifies the
compositor's `moved-to-rect` result, rather than trusting `Gtk.Window.GetPosition`,
and checks the complete tooltip text. Maximized/fullscreen popup rectangles must
fit inside the parent window. Run on Wayland to cover the original failure;
X11 alone does not exercise that backend. It does not use CAN hardware or move
the mouse pointer.

The LIN checks exercise complete ID 57 and 58 definitions at eight upper/lower edge
anchors in each window mode. They check the actual popover bounds, preservation
of full text, a usable scroll range, and stable scroll position through repeated
queries as frames arrive. They also check delayed closing with the pointer-inside
state set and cleared. Physical pointer movement is not synthesized, so manual
hover/scroll confirmation remains useful.

Hover lifecycle checks cover rapid pointer movement, stationary dwell, repeated
content queries, immediate dismissal when entering another table, cancellation
of an abandoned opening timer, and single-popup ownership across both tables.
These drive the same controller methods as the pointer event handlers; physical
pointer movement remains a manual check.

During direct LIN controller checks, automatic GTK tooltip queries are paused
so the real cursor cannot dismiss a popup opened at a synthetic anchor. The
checks still exercise placement, scrolling and controller hover/close behavior;
physical pointer interaction remains a manual check.
