// UnityWebBrowser (UWB)
// Copyright (c) 2021-2024 Voltstro-Studios
// 
// This project is under the MIT license.See the LICENSE.md file for more details.

using VoltstroStudios.UnityWebBrowser.Shared;
using Xilium.CefGlue;

namespace UnityWebBrowser.Engine.Cef.Shared.Browser;

internal static class UwbCefClientUtils
{
    public static CefEventFlags GetKeyDirection(WindowsKey key) => key switch
    {
        WindowsKey.LShiftKey | WindowsKey.LControlKey | WindowsKey.LMenu => CefEventFlags.IsLeft,
        WindowsKey.RShiftKey | WindowsKey.RControlKey | WindowsKey.RMenu => CefEventFlags.ShiftDown,
        _ => CefEventFlags.None
    };
    
#if MACOS
    /// <summary>
    ///     Converts a <see cref="WindowsKey" /> to a macOS Carbon virtual keycode (kVK_*).
    /// </summary>
    /// <remarks>
    ///     On macOS, CEF relies on <c>native_key_code</c> to resolve a DomCode for the key event.
    ///     Keys that produce no printable character (Backspace, Enter, arrows, ...) only travel
    ///     through the KeyDown/KeyUp path, so with <c>native_key_code</c> left at 0 Blink cannot
    ///     build an edit command and silently drops the event. On Windows the Win32 virtual key
    ///     in <c>windows_key_code</c> happens to double as the native code, which is why this
    ///     only affects macOS.
    ///     <para>
    ///         Values are the layout-independent keycodes from Carbon's &lt;HIToolbox/Events.h&gt;.
    ///         Unmapped keys return 0, preserving the previous behaviour.
    ///     </para>
    /// </remarks>
    public static int GetMacNativeKeyCode(WindowsKey key) => key switch
    {
        WindowsKey.Back => 0x33,     // kVK_Delete (labelled Backspace on macOS)
        WindowsKey.Delete => 0x75,   // kVK_ForwardDelete
        WindowsKey.Return => 0x24,   // kVK_Return (WindowsKey.Enter is an alias of Return)
        WindowsKey.Tab => 0x30,      // kVK_Tab
        WindowsKey.Escape => 0x35,   // kVK_Escape
        WindowsKey.Space => 0x31,    // kVK_Space
        WindowsKey.Left => 0x7B,     // kVK_LeftArrow
        WindowsKey.Right => 0x7C,    // kVK_RightArrow
        WindowsKey.Down => 0x7D,     // kVK_DownArrow
        WindowsKey.Up => 0x7E,       // kVK_UpArrow
        WindowsKey.Home => 0x73,     // kVK_Home
        WindowsKey.End => 0x77,      // kVK_End
        WindowsKey.Prior => 0x74,    // kVK_PageUp (WindowsKey.PageUp is an alias of Prior)
        WindowsKey.Next => 0x79,     // kVK_PageDown (WindowsKey.PageDown is an alias of Next)
        _ => 0
    };
#endif

    public static CefEventFlags KeyToFlag(WindowsKey key) => key switch
    {
        // Stateful keys
        WindowsKey.CapsLock => CefEventFlags.CapsLockOn,
        WindowsKey.NumLock => CefEventFlags.NumLockOn,

        WindowsKey.Shift => CefEventFlags.ShiftDown,
        WindowsKey.ShiftKey => CefEventFlags.ShiftDown,
        WindowsKey.LShiftKey => CefEventFlags.ShiftDown,
        WindowsKey.RShiftKey => CefEventFlags.ShiftDown,

        WindowsKey.Control => CefEventFlags.ControlDown,
        WindowsKey.ControlKey => CefEventFlags.ControlDown,
        WindowsKey.LControlKey => CefEventFlags.ControlDown,
        WindowsKey.RControlKey => CefEventFlags.ControlDown,

        WindowsKey.Alt => CefEventFlags.AltGrDown,
        WindowsKey.Menu => CefEventFlags.AltDown,
        WindowsKey.LMenu => CefEventFlags.AltDown,
        WindowsKey.RMenu => CefEventFlags.AltDown,
        // No support for command

        _ => CefEventFlags.None
    };
}