using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WhoIsMarkdown.App;

/// <summary>
/// Preserves native move, resize and window-state behavior for WIMD's custom
/// title bar, which lets the user-selected local background span the full window.
/// </summary>
public partial class MainWindow
{
    private const int WindowMessageGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private HwndSource? chromeWindowSource;

    private void Window_SourceInitialized(object? sender, EventArgs eventArgs)
    {
        chromeWindowSource = PresentationSource.FromVisual(this) as HwndSource;
        chromeWindowSource?.AddHook(WindowChromeMessageHook);
    }

    private nint WindowChromeMessageHook(
        nint windowHandle,
        int message,
        nint wordParameter,
        nint longParameter,
        ref bool handled)
    {
        if (message != WindowMessageGetMinMaxInfo || longParameter == nint.Zero)
        {
            return nint.Zero;
        }

        ApplyMonitorWorkArea(windowHandle, longParameter);
        handled = true;
        return nint.Zero;
    }

    /// <summary>
    /// Bug fix: a borderless WindowChrome window otherwise maximizes to the full
    /// monitor rectangle and covers the Windows taskbar. WM_GETMINMAXINFO must use
    /// the current monitor's work area so multi-monitor and non-bottom taskbars
    /// retain the same behavior as a native framed window.
    /// </summary>
    private static void ApplyMonitorWorkArea(nint windowHandle, nint minMaxInfoPointer)
    {
        nint monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitorHandle == nint.Zero)
        {
            return;
        }

        MonitorInfo monitorInfo = new()
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>(),
        };
        if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return;
        }

        MinMaxInfo minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(minMaxInfoPointer);
        minMaxInfo.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left;
        minMaxInfo.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top;
        minMaxInfo.MaxSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        minMaxInfo.MaxSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        Marshal.StructureToPtr(minMaxInfo, minMaxInfoPointer, false);
    }

    private void DisposeWindowChrome()
    {
        if (chromeWindowSource is null)
        {
            return;
        }

        chromeWindowSource.RemoveHook(WindowChromeMessageHook);
        chromeWindowSource = null;
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs eventArgs)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs eventArgs)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs eventArgs)
    {
        Close();
    }

    private void Window_StateChanged(object? sender, EventArgs eventArgs)
    {
        if (MaximizeRestoreGlyph is null)
        {
            return;
        }

        MaximizeRestoreGlyph.Text = WindowState == WindowState.Maximized
            ? "\uE923"
            : "\uE922";
    }

    // DllImport is intentionally limited to these two stable Win32 APIs. Using
    // LibraryImport would force WPF's generated temporary project to enable
    // unsafe compilation solely for a window-sizing hook.
#pragma warning disable SYSLIB1054
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitorHandle, ref MonitorInfo monitorInfo);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;

        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;

        public NativePoint MaxSize;

        public NativePoint MaxPosition;

        public NativePoint MinTrackSize;

        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;

        public NativeRectangle MonitorArea;

        public NativeRectangle WorkArea;

        public uint Flags;
    }
}
