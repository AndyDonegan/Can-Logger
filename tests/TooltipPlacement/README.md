# Tooltip placement regression check

Run from the project root on a Linux GTK desktop:

```bash
dotnet run --project tests/TooltipPlacement/TooltipPlacement.csproj
```

If only a newer .NET runtime is installed, prefix the command with
`DOTNET_ROLL_FORWARD=Major`.

The check opens temporary CAN Logger windows in windowed, maximized and fullscreen
modes. It uses the real tooltip handlers for both views at left, middle and right
positions, including updates while the tooltip remains open. It verifies the
compositor's `moved-to-rect` result, rather than trusting `Gtk.Window.GetPosition`,
and checks the complete tooltip text. Maximized/fullscreen popup rectangles must
fit inside the parent window. Run on Wayland to cover the original failure;
X11 alone does not exercise that backend. It does not use CAN hardware or move
the mouse pointer.
