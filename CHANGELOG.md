## v0.1.1

#### What's New
- Added a decoupled cross-platform input architecture (`IInputProvider`) based on the Strategy Pattern
- Added Windows input provider (`User32InputProvider`) using HWND-based focus tracking to support both classic console and tabbed Windows Terminal
- Added Linux input provider (`LibX11InputProvider`) with layout-independent key mapping and X11 window tree traversal focus checks
- Added a fallback input provider (`DotNetInputProvider`) with a timeout-based KeyUp emulator for headless or Wayland environments
- Added Wayland environment checks to gracefully bypass X11 global polling on modern Linux desktops
- Added a static `Input.IsPollingEnabled` toggle to temporarily suspend global input polling

#### What's Improved
- Improved resource disposal by subscribing to CLR event hooks (`ProcessExit` and `CancelKeyPress`) to clean up unmanaged resources on forced termination

#### What's Fixed
- Fixed a bug with the `Vector3` Z-coordinate zero-value calculation

## v0.1.0
- Initial release (First Public Version)