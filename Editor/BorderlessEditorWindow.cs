#if UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;

namespace AFreoN.BorderlessEditor
{
    /// <summary>
    /// Removes the Windows title bar from the Unity main window. Window buttons (title bar / minimize / close)
    /// live in the dark menu row, or in Unity's main toolbar when the dark menu bar is off.
    /// Menu: Window > Borderless Editor
    /// </summary>
    [InitializeOnLoad]
    public static class BorderlessEditorWindow
    {
        const string MenuRoot = "Window/Borderless Editor/";
        const string EnabledPref = "AFreoN.BorderlessEditor.Enabled";
        const string ToolbarPath = "Window Controls/";

        static IntPtr s_Hwnd;
        static double s_NextCheckTime;
        static double s_NextToolbarCheck;

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPref, false);
            set { EditorPrefs.SetBool(EnabledPref, value); Apply(); RefreshToolbar(); }
        }

        static BorderlessEditorWindow()
        {
            // Wait until the main window exists (editor startup / domain reload).
            EditorApplication.delayCall += Apply;
            EditorApplication.update += Update;
        }

        #region Menu

        [MenuItem(MenuRoot + "Hide Title Bar %#F11", priority = 2000)]
        internal static void ToggleEnabled() => Enabled = !Enabled;

        [MenuItem(MenuRoot + "Hide Title Bar %#F11", true)]
        static bool ToggleEnabledValidate() { Menu.SetChecked(MenuRoot + "Hide Title Bar", Enabled); return true; }

        internal static void Minimize()
        {
            var hwnd = GetMainWindow();
            if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_MINIMIZE);
        }

        #endregion

        #region Main toolbar buttons (Unity 6.3+)

        // One element holding all three buttons keeps them together and in a fixed order.
        [MainToolbarElement(ToolbarPath + "Buttons", defaultDockPosition = MainToolbarDockPosition.Right, defaultDockIndex = 1000)]
        static IEnumerable<MainToolbarElement> WindowButtons()
        {
            yield return new MainToolbarButton(new MainToolbarContent(Enabled ? "□" : "▣",
                Enabled ? "Show Windows title bar" : "Hide Windows title bar (borderless)"), ToggleEnabled);
            yield return new MainToolbarButton(new MainToolbarContent("—", "Minimize"), Minimize);
            yield return new MainToolbarButton(new MainToolbarContent("✕", "Close Unity"), Close);
        }

        static void RefreshToolbar() => MainToolbar.Refresh(ToolbarPath + "Buttons");

        // The toolbar copy of the window buttons is only needed when the title bar is hidden and the dark menu
        // row (which has its own buttons) is off. Unity registers custom toolbar elements hidden, and the
        // overlays don't exist yet right after a domain reload, so this is re-checked periodically.
        static void UpdateToolbarButtons()
        {
            if (EditorApplication.timeSinceStartup < s_NextToolbarCheck) return;
            s_NextToolbarCheck = EditorApplication.timeSinceStartup + 1;
            SetToolbarButtonsDisplayed(Enabled && !DarkMenuBar.IsActive);
        }

        static void SetToolbarButtonsDisplayed(bool displayed)
        {
            var windowType = typeof(Editor).Assembly.GetType("UnityEditor.MainToolbarWindow");
            var windows = windowType != null ? Resources.FindObjectsOfTypeAll(windowType) : null;
            if (windows == null || windows.Length == 0) return;
            if (((EditorWindow)windows[0]).TryGetOverlay(ToolbarPath + "Buttons", out var overlay) && overlay.displayed != displayed)
                overlay.displayed = displayed;
        }

        #endregion

        #region Window actions

        // WM_CLOSE goes through Unity's normal quit flow, including "save modified scenes?" prompts.
        internal static void Close()
        {
            var hwnd = GetMainWindow();
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Start a native window move, as if the title bar were dragged. Call while the left button is down.</summary>
        internal static void BeginWindowDrag()
        {
            var hwnd = GetMainWindow();
            if (hwnd == IntPtr.Zero || !GetCursorPos(out var cursor)) return;
            // Windows won't move a maximized window. In borderless mode the window just fills the work area,
            // so drop the maximized flag; after the drop Update re-fits it to the monitor it landed on.
            if (Enabled)
            {
                long style = GetStyle(hwnd);
                if ((style & WS_MAXIMIZE) != 0) SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style & ~WS_MAXIMIZE));
            }
            ReleaseCapture();
            PostMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), new IntPtr((cursor.Y << 16) | (cursor.X & 0xFFFF)));
        }

        /// <summary>Title-bar double-click: maximize/restore, or leave borderless mode when it's on.</summary>
        internal static void ToggleMaximize()
        {
            if (Enabled) { Enabled = false; return; }
            var hwnd = GetMainWindow();
            if (hwnd != IntPtr.Zero) ShowWindow(hwnd, IsZoomed(hwnd) ? SW_RESTORE : SW_MAXIMIZE);
        }

        static void Apply()
        {
            var hwnd = GetMainWindow();
            if (hwnd == IntPtr.Zero || IsIconic(hwnd)) return;

            long style = GetStyle(hwnd);

            if (!Enabled)
            {
                if ((style & WS_CAPTION) == WS_CAPTION && (style & WS_THICKFRAME) != 0) return;
                SetStyle(hwnd, style | WS_CAPTION | WS_THICKFRAME);
                // Re-run maximize so Windows recomputes the frame geometry.
                ShowWindow(hwnd, SW_RESTORE);
                ShowWindow(hwnd, SW_MAXIMIZE);
                return;
            }

            long target = style & ~(WS_CAPTION | WS_THICKFRAME);

            var work = GetWorkArea(hwnd);
            GetWindowRect(hwnd, out var rect);
            bool rectOk = rect.Equals(work);

            if (target == style && rectOk) return;

            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(target));
            SetWindowPos(hwnd, IntPtr.Zero, work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top,
                SWP_FRAMECHANGED | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        static void Update()
        {
            UpdateToolbarButtons();
            if (!Enabled) return;

            // Periodically re-fit: restoring from the taskbar or changing displays can make Windows
            // maximize a captionless window over the whole monitor (covering the taskbar).
            // Not while a mouse button is held: the user may be dragging the window to another monitor.
            if (EditorApplication.timeSinceStartup >= s_NextCheckTime && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
            {
                s_NextCheckTime = EditorApplication.timeSinceStartup + 0.5;
                Apply();
            }
        }

        #endregion

        #region Win32 helpers

        internal static IntPtr GetMainWindow()
        {
            if (s_Hwnd != IntPtr.Zero && IsWindow(s_Hwnd)) return s_Hwnd;

            s_Hwnd = IntPtr.Zero;
            uint pid = (uint)Process.GetCurrentProcess().Id;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint windowPid);
                if (windowPid != pid || !IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;

                var title = new StringBuilder(512);
                GetWindowText(h, title, title.Capacity);
                // Main window title: "<Project> - <Scene> - <Platform> - Unity 6.x ... <DX11>"
                if (title.ToString().Contains(" - Unity ")) { s_Hwnd = h; return false; }
                return true;
            }, IntPtr.Zero);

            return s_Hwnd;
        }

        static RECT GetWorkArea(IntPtr hwnd)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info);
            return info.rcWork;
        }

        static long GetStyle(IntPtr hwnd) => GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();

        static void SetStyle(IntPtr hwnd, long style)
        {
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        const int GWL_STYLE = -16;
        const long WS_CAPTION = 0x00C00000L;
        const long WS_THICKFRAME = 0x00040000L;
        const long WS_MAXIMIZE = 0x01000000L;
        const uint WM_NCLBUTTONDOWN = 0x00A1;
        const int HTCAPTION = 2, VK_LBUTTON = 0x01;
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004,
            SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020, SWP_NOOWNERZORDER = 0x0200;
        const int SW_MAXIMIZE = 3, SW_MINIMIZE = 6, SW_RESTORE = 9;
        const uint WM_CLOSE = 0x0010, GW_OWNER = 4, MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

        #endregion
    }
}
#endif
