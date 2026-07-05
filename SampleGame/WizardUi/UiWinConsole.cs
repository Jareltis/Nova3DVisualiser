using System;
using System.Runtime.InteropServices;

namespace SampleGame.WizardUi;

// A normalized input event from the console: a key press or a left-mouse click (in cell coordinates). `None`
// is a consumed-but-ignored record (key-up, mouse move/wheel, focus/resize) — the loop drains it and moves on.
public enum UiInputKind { None, Key, MouseClick }

public struct UiInputEvent
{
    public UiInputKind Kind;
    public ConsoleKeyInfo Key;
    public int MouseX, MouseY;
}

// The Win32 console-input layer for the wizard UI (mouse support): enable ENABLE_MOUSE_INPUT (+
// ENABLE_EXTENDED_FLAGS, and CLEAR ENABLE_QUICK_EDIT_MODE so QuickEdit doesn't swallow clicks for text
// selection), then read INPUT_RECORDs via ReadConsoleInput — handling BOTH key events (mapped to
// ConsoleKeyInfo, identical to the keyboard path) AND left-button clicks. The original console mode is saved
// and RESTORED on exit. Everything is Windows-only + guarded; off-Windows (or if enabling fails) TryReadEvent
// falls back to the Console.ReadKey path (keyboard only), so the UI still runs. NOTE: with mouse input on,
// conhost hands Ctrl+wheel to the app, so Ctrl+scroll doesn't font-zoom the wizard (use a window resize or
// Ctrl+=/Ctrl+-); the in-game 3D, which doesn't enable mouse input, still zooms on Ctrl+scroll. VT/ANSI mouse
// for non-Windows terminals is a future cross-platform follow-up (not built here).
public static class UiWinConsole
{
    private const int STD_INPUT_HANDLE = -10;
    private const uint ENABLE_MOUSE_INPUT = 0x0010;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;

    private const ushort KEY_EVENT = 0x0001;
    private const ushort MOUSE_EVENT = 0x0002;

    private const uint FROM_LEFT_1ST_BUTTON_PRESSED = 0x0001;
    private const uint MOUSE_MOVED = 0x0001;
    private const uint MOUSE_WHEELED = 0x0004;
    private const uint MOUSE_HWHEELED = 0x0008;

    private const uint SHIFT_PRESSED = 0x0010;
    private const uint LEFT_ALT_PRESSED = 0x0002;
    private const uint RIGHT_ALT_PRESSED = 0x0001;
    private const uint LEFT_CTRL_PRESSED = 0x0008;
    private const uint RIGHT_CTRL_PRESSED = 0x0004;

    private static bool _mouseEnabled;
    private static uint _savedMode;
    private static IntPtr _hIn = IntPtr.Zero;
    private static bool _prevLeftDown;

    // Enable mouse input on the console (Windows). Saves the original mode so RestoreMouse can put it back.
    // Must set ENABLE_EXTENDED_FLAGS for the QuickEdit change to take effect. Idempotent + guarded.
    public static void EnableMouse()
    {
        if (_mouseEnabled || !OperatingSystem.IsWindows()) return;
        try
        {
            _hIn = GetStdHandle(STD_INPUT_HANDLE);
            if (_hIn == IntPtr.Zero || _hIn == new IntPtr(-1)) return;
            if (!GetConsoleMode(_hIn, out uint mode)) return;
            _savedMode = mode;
            uint newMode = (mode | ENABLE_MOUSE_INPUT | ENABLE_EXTENDED_FLAGS) & ~ENABLE_QUICK_EDIT_MODE;
            if (SetConsoleMode(_hIn, newMode)) { _mouseEnabled = true; _prevLeftDown = false; }
        }
        catch { /* best effort — fall back to keyboard-only */ }
    }

    // Restore the console input mode saved by EnableMouse (re-enables QuickEdit etc.). Idempotent + guarded.
    public static void RestoreMouse()
    {
        if (!_mouseEnabled) return;
        try { SetConsoleMode(_hIn, _savedMode); } catch { }
        _mouseEnabled = false;
    }

    // Pure click parse (testable): from a mouse event's button state + flags + the PREVIOUS left-down state,
    // is this a left-button DOWN-transition (a click)? `nowLeftDown` returns the current left-down state so the
    // caller can carry it forward. A move/wheel event, a release, or a held button is NOT a click.
    public static bool IsLeftClick(uint buttonState, uint eventFlags, bool prevLeftDown, out bool nowLeftDown)
    {
        nowLeftDown = (buttonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0;
        bool isButtonChange = (eventFlags & (MOUSE_MOVED | MOUSE_WHEELED | MOUSE_HWHEELED)) == 0;
        return isButtonChange && nowLeftDown && !prevLeftDown;   // up -> down transition
    }

    // Non-blocking: reads the next pending console input event, if any. Returns false when the queue is empty
    // (so the loop keeps polling the size for font-zoom). On Windows (mouse enabled) reads via ReadConsoleInput
    // and yields a Key (key-down), a MouseClick (left down-transition), or None (ignored record). Otherwise
    // falls back to Console.KeyAvailable/ReadKey (keyboard only). Guarded — a transient failure returns false.
    public static bool TryReadEvent(out UiInputEvent ev)
    {
        ev = default;

        if (!OperatingSystem.IsWindows() || !_mouseEnabled)
        {
            try
            {
                if (!Console.KeyAvailable) return false;
                ev = new UiInputEvent { Kind = UiInputKind.Key, Key = Console.ReadKey(true) };
                return true;
            }
            catch { return false; }
        }

        try
        {
            if (!GetNumberOfConsoleInputEvents(_hIn, out uint pending) || pending == 0) return false;
            var buf = new INPUT_RECORD[1];
            if (!ReadConsoleInput(_hIn, buf, 1, out uint read) || read == 0) return false;

            var rec = buf[0];
            if (rec.EventType == KEY_EVENT)
            {
                var k = rec.KeyEvent;
                if (k.bKeyDown != 0)
                {
                    ev = new UiInputEvent { Kind = UiInputKind.Key, Key = ToKeyInfo(k) };
                    return true;
                }
                ev = new UiInputEvent { Kind = UiInputKind.None };   // key-up: consumed, ignored
                return true;
            }
            if (rec.EventType == MOUSE_EVENT)
            {
                var m = rec.MouseEvent;
                bool click = IsLeftClick(m.dwButtonState, m.dwEventFlags, _prevLeftDown, out bool nowDown);
                _prevLeftDown = nowDown;
                if (click)
                {
                    // dwMousePosition is BUFFER-relative, but our VT cursor positioning (and thus the widget
                    // layout) is VIEWPORT-relative — so subtract the window origin (srWindow.Left/Top) to get
                    // the on-screen cell. Off-top-of-buffer this is 0,0 and the subtraction is a no-op; when
                    // the console is scrolled it keeps clicks aligned with the drawn widgets.
                    int col = m.dwMousePosition.X, row = m.dwMousePosition.Y;
                    if (TryGetWindowOrigin(out int ox, out int oy)) { col -= ox; row -= oy; }
                    ev = new UiInputEvent { Kind = UiInputKind.MouseClick, MouseX = col, MouseY = row };
                    return true;
                }
                ev = new UiInputEvent { Kind = UiInputKind.None };
                return true;
            }

            ev = new UiInputEvent { Kind = UiInputKind.None };   // focus / resize / menu: ignored
            return true;
        }
        catch { return false; }
    }

    // Maps a native key event to a ConsoleKeyInfo (ConsoleKey's values ARE the Win32 virtual-key codes, so the
    // cast is exact for the keys the UI uses: arrows, Tab, Enter, Space, Escape, letters).
    private static ConsoleKeyInfo ToKeyInfo(KEY_EVENT_RECORD k)
    {
        char ch = (char)k.UnicodeChar;
        var key = (ConsoleKey)k.wVirtualKeyCode;
        bool shift = (k.dwControlKeyState & SHIFT_PRESSED) != 0;
        bool alt = (k.dwControlKeyState & (LEFT_ALT_PRESSED | RIGHT_ALT_PRESSED)) != 0;
        bool ctrl = (k.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED)) != 0;
        return new ConsoleKeyInfo(ch, key, shift, alt, ctrl);
    }

    // The console viewport's top-left cell within the screen buffer (srWindow.Left/Top), for buffer->viewport
    // mouse mapping. Reads the OUTPUT handle; returns false (0,0) if unavailable.
    private const int STD_OUTPUT_HANDLE = -11;
    private static bool TryGetWindowOrigin(out int left, out int top)
    {
        left = 0; top = 0;
        try
        {
            var hOut = GetStdHandle(STD_OUTPUT_HANDLE);
            if (hOut == IntPtr.Zero || hOut == new IntPtr(-1)) return false;
            if (!GetConsoleScreenBufferInfo(hOut, out var info)) return false;
            left = info.srWindow.Left; top = info.srWindow.Top;
            return true;
        }
        catch { return false; }
    }

    // ---- Win32 interop ----
    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT { public short Left; public short Top; public short Right; public short Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public short wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEY_EVENT_RECORD
    {
        public int bKeyDown;            // BOOL
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public ushort UnicodeChar;      // union uChar
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSE_EVENT_RECORD
    {
        public COORD dwMousePosition;
        public uint dwButtonState;
        public uint dwControlKeyState;
        public uint dwEventFlags;
    }

    // INPUT_RECORD is a tagged union — EventType then an overlaid Event. Overlay the two records we read at the
    // union offset (4: EventType is a WORD but the union is DWORD-aligned). Both are blittable value structs.
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
        [FieldOffset(4)] public MOUSE_EVENT_RECORD MouseEvent;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNumberOfConsoleInputEvents(IntPtr hConsoleInput, out uint lpcNumberOfEvents);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadConsoleInput(IntPtr hConsoleInput, [Out] INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsRead);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO info);
}
