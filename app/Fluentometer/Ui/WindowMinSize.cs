using System;
using System.Runtime.InteropServices;

namespace Fluentometer.Ui;

/// <summary>
/// Enforces a minimum size on an unpackaged WinUI 3 window by subclassing its HWND and
/// handling WM_GETMINMAXINFO. WinUI 3 / <c>OverlappedPresenter</c> exposes no minimum-size API,
/// so this interop subclass is the standard approach. The minimum is given in DPI-independent
/// (effective) pixels and converted to physical pixels using the window's current DPI, so the
/// floor holds across monitors with different scaling.
///
/// This is not merely cosmetic. The dashboard's <c>ItemsRepeater</c>/<c>UniformGridLayout</c>
/// (ItemsStretch=Fill, capped to one column) computes an items-per-line count of
/// <c>floor(availableWidth / MinItemWidth)</c> and then divides the available width by it. When
/// the available width falls below <c>MinItemWidth</c> that count is 0, and the divide throws
/// <see cref="DivideByZeroException"/> inside MeasureOverride — a fatal stowed exception that
/// crashes the process. A window-width floor keeps the available width out of that degenerate
/// range for every interactive resize. See
/// summaries/2026-06-17-uniformgridlayout-divbyzero-on-resize.md.
/// </summary>
public static class WindowMinSize
{
    private const uint WM_GETMINMAXINFO = 0x0024;

    // The native callback must outlive the window; if the GC collects the delegate the next
    // WM_GETMINMAXINFO calls freed memory. One window per process → a static field is enough.
    private static SUBCLASSPROC? _proc;

    private delegate IntPtr SUBCLASSPROC(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    /// <summary>
    /// Installs a minimum-size constraint on <paramref name="hwnd"/>. The width/height are in
    /// effective (DPI-independent) pixels and are scaled to the window's current DPI on each query.
    /// </summary>
    public static void Apply(IntPtr hwnd, int minWidth, int minHeight)
    {
        _proc = (h, msg, w, l, id, data) =>
        {
            if (msg == WM_GETMINMAXINFO)
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(l);
                var scale = GetDpiForWindow(h) / 96.0;
                mmi.ptMinTrackSize.X = (int)(minWidth * scale);
                mmi.ptMinTrackSize.Y = (int)(minHeight * scale);
                Marshal.StructureToPtr(mmi, l, false);
                return IntPtr.Zero;
            }
            return DefSubclassProc(h, msg, w, l);
        };
        SetWindowSubclass(hwnd, _proc, UIntPtr.Zero, UIntPtr.Zero);
    }
}
