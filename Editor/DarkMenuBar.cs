#if UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace AFreoN.BorderlessEditor
{
    /// <summary>
    /// Replaces the light native Windows menu bar with a dark menu row drawn by Unity above the main toolbar.
    /// Menus open as dark popup windows (submenus, shortcuts, checkmarks, disabled items, keyboard navigation)
    /// and run the same commands via EditorApplication.ExecuteMenuItem.
    /// Relies on Unity internals (MainView layout, Menu.GetMenuItems); on any failure it switches itself off
    /// and restores the native menu bar.
    /// Menu: Window > Borderless Editor > Dark Menu Bar
    /// </summary>
    [InitializeOnLoad]
    public static class DarkMenuBar
    {
        const string MenuPath = "Window/Borderless Editor/Dark Menu Bar";
        const string EnabledPref = "AFreoN.BorderlessEditor.DarkMenuBar.Enabled";
        const string RootsKey = "AFreoN.BorderlessEditor.DarkMenuBar.Roots";
        const string NativeMenuKey = "AFreoN.BorderlessEditor.DarkMenuBar.NativeMenu";
        const string RowName = "borderless-dark-menu-bar";
        const float RowHeight = 22f;
        const float FallbackToolbarHeight = 36f;

        static readonly Assembly s_EditorAssembly = typeof(Editor).Assembly;
        static readonly Type s_ViewType = s_EditorAssembly.GetType("UnityEditor.View");
        static readonly Type s_MainViewType = s_EditorAssembly.GetType("UnityEditor.MainView");
        static readonly Type s_ToolbarWindowType = s_EditorAssembly.GetType("UnityEditor.MainToolbarWindow");
        const BindingFlags k_Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags k_Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static UnityEngine.Object s_MainView;
        static EditorWindow s_Toolbar;
        static VisualElement s_Row;
        static readonly Dictionary<string, VisualElement> s_RootButtons = new Dictionary<string, VisualElement>();
        static bool s_Failed;

        internal static string OpenRoot { get; private set; }
        static string s_LastAutoClosedRoot;
        static double s_LastAutoCloseTime;
        static int s_LostFocusTicks;
        static bool s_Opening;
        static string s_PressedRoot;

        /// <summary>On and working (not switched off after a failure).</summary>
        internal static bool IsActive => Enabled && !s_Failed;

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPref, false);
            set
            {
                EditorPrefs.SetBool(EnabledPref, value);
                s_Failed = false;
                if (!value) Restore();
            }
        }

        static DarkMenuBar()
        {
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                // The window hook, UI and popups hold delegates into this domain; drop them first.
                // Layout + hidden native menu survive the reload.
                RemoveHook();
                CloseAll();
                RemoveRow();
            };
            EditorApplication.quitting += RemoveHook;
        }

        [MenuItem(MenuPath, priority = 2010)]
        static void Toggle() => Enabled = !Enabled;

        [MenuItem(MenuPath, true)]
        static bool ToggleValidate() { Menu.SetChecked(MenuPath, Enabled); return true; }

        #region Maintenance loop

        static void Update()
        {
            if (!Enabled || s_Failed) { s_HookHiding = false; return; }
            try
            {
                EnsureHook();
                s_HookHiding = true;
                HideNativeMenu();
                TrackWorkWindow();
                EnsureRow();
                EnsureLayout();
                UpdateTitle();
                UpdateCaptionButtons();
                WatchPopupFocus();
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        static void Fail(Exception e)
        {
            s_Failed = true;
            Debug.LogWarning($"[Dark Menu Bar] Disabled for this session, restoring the native menu bar. Reason: {e.Message}\n{e}");
            try { Restore(); } catch (Exception inner) { Debug.LogException(inner); }
        }

        static void Restore()
        {
            RemoveHook();
            CloseAll();
            RemoveRow();
            ResetLayout();
            RestoreNativeMenu();
        }

        #endregion

        #region Native menu

        internal static void HideNativeMenu()
        {
            if (!Enabled || s_Failed) return;
            var hwnd = BorderlessEditorWindow.GetMainWindow();
            if (hwnd == IntPtr.Zero) return;

            // Backup path; the window hook normally detaches the menu before it can paint.
            var menu = GetMenu(hwnd);
            if (menu != IntPtr.Zero)
            {
                SetMenu(hwnd, IntPtr.Zero);
                s_DetachedMenu = menu;
            }
            ProcessDetachedMenu();
        }

        // Unity rebuilds its menu when packages add menus; only rebuild our row if the top-level names changed.
        static void ProcessDetachedMenu()
        {
            var menu = s_DetachedMenu;
            if (menu == IntPtr.Zero) return;
            s_DetachedMenu = IntPtr.Zero;
            SessionState.SetString(NativeMenuKey, menu.ToInt64().ToString());

            var names = new List<string>();
            int count = GetMenuItemCount(menu);
            for (int i = 0; i < count; i++)
            {
                var sb = new StringBuilder(256);
                GetMenuStringW(menu, (uint)i, sb, sb.Capacity, MF_BYPOSITION);
                var name = sb.ToString().Replace("&", "");
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }

            var joined = string.Join("\n", names);
            if (names.Count > 0 && joined != SessionState.GetString(RootsKey, ""))
            {
                SessionState.SetString(RootsKey, joined);
                CloseAll();
                RemoveRow();
            }
        }

        #endregion

        #region Main window message hook

        // Unity re-attaches its native menu whenever the main window gains or loses activation (e.g. every time
        // a menu popup opens or closes). Polling can't hide it before it paints, so we subclass the main window
        // and detach the menu right after Unity handles each message, before anything is drawn.
        // The hook must never outlive this domain: it is removed before assembly reload, on quit and on Restore.

        delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        static WndProcDelegate s_HookDelegate; // kept referenced while installed so it isn't collected
        static IntPtr s_HookPointer;
        static IntPtr s_HookedHwnd;
        static IntPtr s_OriginalWndProc;
        static volatile bool s_HookHiding;
        static bool s_InHook;
        static IntPtr s_DetachedMenu;

        static void EnsureHook()
        {
            var hwnd = BorderlessEditorWindow.GetMainWindow();
            if (hwnd == IntPtr.Zero || hwnd == s_HookedHwnd) return;

            RemoveHook(); // main window was recreated
            s_HookDelegate = HookProc;
            s_HookPointer = Marshal.GetFunctionPointerForDelegate(s_HookDelegate);
            var original = SetWindowLongPtr(hwnd, GWLP_WNDPROC, s_HookPointer);
            if (original == IntPtr.Zero) { s_HookDelegate = null; s_HookPointer = IntPtr.Zero; return; }
            s_OriginalWndProc = original;
            s_HookedHwnd = hwnd;
        }

        static void RemoveHook()
        {
            s_HookHiding = false;
            if (s_HookedHwnd != IntPtr.Zero && s_OriginalWndProc != IntPtr.Zero && IsWindow(s_HookedHwnd))
                SetWindowLongPtr(s_HookedHwnd, GWLP_WNDPROC, s_OriginalWndProc);
            s_HookedHwnd = IntPtr.Zero;
            s_OriginalWndProc = IntPtr.Zero;
            // s_HookDelegate stays referenced: a call already on the stack may still be returning through it.
        }

        // Keep this minimal and exception-free: it runs for every message the main window receives.
        static IntPtr HookProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            var original = s_OriginalWndProc;
            var result = CallWindowProc(original, hWnd, msg, wParam, lParam);

            if (msg == WM_NCDESTROY)
            {
                if (hWnd == s_HookedHwnd) { s_HookedHwnd = IntPtr.Zero; s_OriginalWndProc = IntPtr.Zero; }
            }
            else if (s_HookHiding && !s_InHook)
            {
                var menu = GetMenu(hWnd);
                if (menu != IntPtr.Zero)
                {
                    s_InHook = true;
                    SetMenu(hWnd, IntPtr.Zero);
                    s_DetachedMenu = menu;
                    s_InHook = false;
                }
            }
            return result;
        }

        #endregion

        #region Native menu (restore)

        static void RestoreNativeMenu()
        {
            RemoveHook();
            var hwnd = BorderlessEditorWindow.GetMainWindow();
            if (hwnd == IntPtr.Zero || GetMenu(hwnd) != IntPtr.Zero) return;

            if (long.TryParse(SessionState.GetString(NativeMenuKey, ""), out var saved) && IsMenu(new IntPtr(saved)))
            {
                SetMenu(hwnd, new IntPtr(saved));
                DrawMenuBar(hwnd);
            }
            else
            {
                // Handle is gone; ask Unity to rebuild (and re-attach) its menus.
                typeof(Menu).GetMethod("RebuildAllMenus", k_Static)?.Invoke(null, null);
            }
        }

        static string[] RootNames =>
            SessionState.GetString(RootsKey, "File\nEdit\nAssets\nGameObject\nComponent\nWindow\nHelp").Split('\n');

        #endregion

        #region Toolbar row

        static EditorWindow Toolbar
        {
            get
            {
                if (s_Toolbar == null)
                {
                    if (s_ToolbarWindowType == null) throw new Exception("UnityEditor.MainToolbarWindow not found.");
                    var found = Resources.FindObjectsOfTypeAll(s_ToolbarWindowType);
                    s_Toolbar = found.Length > 0 ? (EditorWindow)found[0] : null;
                }
                return s_Toolbar;
            }
        }

        static void EnsureRow()
        {
            var toolbar = Toolbar;
            if (toolbar == null) return;
            var root = toolbar.rootVisualElement;
            if (s_Row != null && s_Row.parent == root) return;

            root.Q(RowName)?.RemoveFromHierarchy();
            s_Row = BuildRow();
            root.Add(s_Row);
            root.style.paddingTop = RowHeight; // push Unity's toolbar content below our row
            root.UnregisterCallback<GeometryChangedEvent>(OnToolbarGeometryChanged);
            root.RegisterCallback<GeometryChangedEvent>(OnToolbarGeometryChanged);
        }

        static bool s_InGeometryFix;

        // Runs during the toolbar's layout pass, before it repaints: undo Unity's relayout (re-attached native
        // menu / reset toolbar height) in the same frame instead of on the next update tick.
        static void OnToolbarGeometryChanged(GeometryChangedEvent e)
        {
            if (!Enabled || s_Failed || s_InGeometryFix) return;
            s_InGeometryFix = true;
            try { HideNativeMenu(); EnsureLayout(); }
            catch (Exception ex) { Fail(ex); }
            finally { s_InGeometryFix = false; }
        }

        static void RemoveRow()
        {
            s_Row?.RemoveFromHierarchy();
            s_Row = null;
            s_RootButtons.Clear();
            s_TitleLabel = null;
            s_TitleBarToggle = null;
            s_MinimizeButton = s_CloseButton = null;
            if (s_Toolbar != null)
            {
                s_Toolbar.rootVisualElement.UnregisterCallback<GeometryChangedEvent>(OnToolbarGeometryChanged);
                s_Toolbar.rootVisualElement.Q(RowName)?.RemoveFromHierarchy();
                s_Toolbar.rootVisualElement.style.paddingTop = StyleKeyword.Null;
            }
        }

        static VisualElement BuildRow()
        {
            var p = DarkMenuPalette.Current;
            var row = new VisualElement { name = RowName };
            row.style.position = Position.Absolute;
            row.style.left = 0; row.style.right = 0; row.style.top = 0;
            row.style.height = RowHeight;
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 4;
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = p.Separator;

            s_RootButtons.Clear();
            foreach (var name in RootNames)
            {
                var rootName = name;
                var button = new Label(rootName);
                button.style.height = RowHeight - 3;
                button.style.paddingLeft = 7; button.style.paddingRight = 7;
                button.style.marginLeft = 0; button.style.marginRight = 0;
                button.style.unityTextAlign = TextAnchor.MiddleCenter;
                button.style.color = p.Text;
                SetRadius(button.style, 3);

                button.RegisterCallback<PointerEnterEvent>(_ =>
                {
                    if (DarkMenuPopup.Chain.Count > 0 && OpenRoot != rootName) Defer(() => OpenMenu(rootName, false)); // measure outside the event, like a click
                    else if (OpenRoot != rootName) button.style.backgroundColor = p.BarHover;
                });
                button.RegisterCallback<PointerLeaveEvent>(_ =>
                {
                    if (OpenRoot != rootName) button.style.backgroundColor = StyleKeyword.Null;
                });
                button.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button != 0) return;
                    s_PressedRoot = null;
                    if (OpenRoot == rootName && DarkMenuPopup.Chain.Count > 0) { Defer(CloseAll); return; }
                    // Clicking the bar steals focus from an open popup, which auto-closes it just before this event.
                    if (s_LastAutoClosedRoot == rootName && EditorApplication.timeSinceStartup - s_LastAutoCloseTime < 0.3) return;
                    s_PressedRoot = rootName;
                });
                // Open on release, and outside the event: creating a popup window while Unity is still handling a
                // click in the main window makes it re-attach and paint its native menu bar for a few frames.
                button.RegisterCallback<PointerUpEvent>(e =>
                {
                    if (e.button != 0 || s_PressedRoot != rootName) return;
                    s_PressedRoot = null;
                    Defer(() => OpenMenu(rootName, false));
                });

                s_RootButtons[rootName] = button;
                row.Add(button);
            }

            row.Add(BuildDragArea(p));
            row.Add(BuildCaptionButtons(p));
            return row;
        }

        #endregion

        #region Title area (drag / double-click / right-click) and window buttons

        static Label s_TitleLabel;
        static VisualElement s_MinimizeButton, s_CloseButton;
        static Label s_TitleBarToggle;
        static bool s_DragArmed;
        static Vector2 s_DragStart;
        static double s_LastTitleClickTime;
        static Vector2 s_LastTitleClickPos;

        // The empty part of the row acts like a title bar and shows project / scene / unsaved state.
        static VisualElement BuildDragArea(DarkMenuPalette p)
        {
            var area = new VisualElement { name = "borderless-dark-menu-title" };
            area.style.flexGrow = 1;
            area.style.alignSelf = Align.Stretch;
            area.style.justifyContent = Justify.Center;
            area.style.overflow = Overflow.Hidden;
            area.style.marginLeft = 12; area.style.marginRight = 12;

            s_TitleLabel = new Label { pickingMode = PickingMode.Ignore };
            s_TitleLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            s_TitleLabel.style.color = p.TextDim;
            s_TitleLabel.style.whiteSpace = WhiteSpace.NoWrap;
            s_TitleLabel.style.overflow = Overflow.Hidden;
            s_TitleLabel.style.textOverflow = TextOverflow.Ellipsis;
            area.Add(s_TitleLabel);
            UpdateTitle();

            area.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                // Own double-click detection (the toolbar panel doesn't report clickCount reliably).
                double now = EditorApplication.timeSinceStartup;
                bool doubleClick = now - s_LastTitleClickTime <= GetDoubleClickTime() / 1000.0 &&
                                   Vector2.Distance(e.position, s_LastTitleClickPos) < 5;
                s_LastTitleClickTime = doubleClick ? 0 : now;
                s_LastTitleClickPos = e.position;
                if (doubleClick) { s_DragArmed = false; Defer(BorderlessEditorWindow.ToggleMaximize); return; }
                s_DragArmed = true;
                s_DragStart = e.position;
                area.CapturePointer(e.pointerId);
            });
            area.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!s_DragArmed || (e.pressedButtons & 1) == 0 || Vector2.Distance(e.position, s_DragStart) < 4) return;
                s_DragArmed = false;
                area.ReleasePointer(e.pointerId);
                BorderlessEditorWindow.BeginWindowDrag();
            });
            area.RegisterCallback<PointerUpEvent>(e =>
            {
                s_DragArmed = false;
                if (area.HasPointerCapture(e.pointerId)) area.ReleasePointer(e.pointerId);
            });
            // The toolbar shows its own "edit toolbar" menu on ContextClickEvent (bubble phase); take it here.
            area.RegisterCallback<ContextClickEvent>(e =>
            {
                e.StopPropagation();
                if (Toolbar == null) return;
                var screen = Toolbar.position.position + e.mousePosition;
                Defer(() => OpenWindowMenu(screen));
            });
            return area;
        }

        static VisualElement BuildCaptionButtons(DarkMenuPalette p)
        {
            var box = new VisualElement();
            box.style.flexDirection = FlexDirection.Row;
            box.style.alignSelf = Align.Stretch;

            s_TitleBarToggle = CaptionButton(box, "", "", p, false, BorderlessEditorWindow.ToggleEnabled);
            s_MinimizeButton = CaptionButton(box, "\u2014", "Minimize", p, false, BorderlessEditorWindow.Minimize);
            s_CloseButton = CaptionButton(box, "\u2715", "Close Unity", p, true, BorderlessEditorWindow.Close);
            s_LastBorderless = null; // force a refresh of glyphs/visibility
            UpdateCaptionButtons();
            return box;
        }

        static Label CaptionButton(VisualElement parent, string glyph, string tooltip, DarkMenuPalette p, bool danger, Action onClick)
        {
            var button = new Label(glyph) { tooltip = tooltip };
            button.style.width = 36;
            button.style.alignSelf = Align.Stretch;
            button.style.marginBottom = 1; // keep the row's bottom border visible
            button.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.style.color = p.Text;
            var hover = danger ? (Color)new Color32(196, 43, 28, 255) : p.BarHover;
            button.RegisterCallback<PointerEnterEvent>(_ => { button.style.backgroundColor = hover; if (danger) button.style.color = Color.white; });
            button.RegisterCallback<PointerLeaveEvent>(_ => { button.style.backgroundColor = StyleKeyword.Null; button.style.color = p.Text; });
            button.RegisterCallback<PointerUpEvent>(e => { if (e.button == 0) Defer(onClick); });
            parent.Add(button);
            return button;
        }

        static bool? s_LastBorderless;

        // Minimize / close only make sense without the native title bar (which has its own buttons).
        static void UpdateCaptionButtons()
        {
            bool borderless = BorderlessEditorWindow.Enabled;
            if (s_TitleBarToggle == null || s_LastBorderless == borderless) return;
            s_LastBorderless = borderless;
            s_TitleBarToggle.text = borderless ? "\u25A1" : "\u25A3";
            s_TitleBarToggle.tooltip = borderless ? "Show Windows title bar" : "Hide Windows title bar (borderless)";
            var display = borderless ? DisplayStyle.Flex : DisplayStyle.None;
            s_MinimizeButton.style.display = display;
            s_CloseButton.style.display = display;
        }

        static void UpdateTitle()
        {
            if (s_TitleLabel == null) return;
            var project = new System.IO.DirectoryInfo(Application.dataPath).Parent?.Name ?? Application.productName;
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var scene = string.IsNullOrEmpty(active.name) ? "Untitled" : active.name;
            bool dirty = false;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                dirty |= UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty;
            var text = project + "  \u2014  " + scene + (dirty ? "  \u2022" : "");
            if (s_TitleLabel.text != text)
            {
                s_TitleLabel.text = text;
                s_TitleLabel.tooltip = dirty ? "Unsaved changes" : "";
            }
        }

        // Right-click on the title area: a dark window menu built from our own menu items.
        static void OpenWindowMenu(Vector2 screenPosition)
        {
            CloseAll();
            var root = new DarkMenuNode { Name = "Window", Path = "", IsSubmenu = true };
            void Add(string name, string path, int priority) =>
                root.Children.Add(new DarkMenuNode { Name = name, Path = path, Priority = priority });
            Add("Hide Title Bar", "Window/Borderless Editor/Hide Title Bar", 0);
            Add("Dark Menu Bar", "Window/Borderless Editor/Dark Menu Bar", 1);
            Add("Close Unity", "File/Exit", 20);

            s_LostFocusTicks = 0;
            s_Opening = true;
            try { DarkMenuPopup.Open(root, new Rect(screenPosition, Vector2.zero), 0); }
            finally { s_Opening = false; }
        }

        static void SetRadius(IStyle s, float r)
        {
            s.borderTopLeftRadius = r; s.borderTopRightRadius = r;
            s.borderBottomLeftRadius = r; s.borderBottomRightRadius = r;
        }

        static void SetRootHighlight(string rootName)
        {
            var p = DarkMenuPalette.Current;
            foreach (var kv in s_RootButtons)
                kv.Value.style.backgroundColor = kv.Key == rootName ? new StyleColor(p.BarOpen) : new StyleColor(StyleKeyword.Null);
        }

        #endregion

        #region Main window layout (toolbar area height)

        static void EnsureLayout()
        {
            var mainView = MainView;
            if (mainView == null) return;

            var children = (Array)Prop(s_ViewType, "children").GetValue(mainView);
            if (children == null || children.Length < 2) return;
            var top = children.GetValue(0);
            var center = children.GetValue(1);
            if (top.GetType().Name != "Toolbar") throw new Exception("Unexpected main window layout: " + top.GetType().Name);

            var position = Prop(s_ViewType, "position");
            var topRect = (Rect)position.GetValue(top);
            float target = BaseToolbarHeight(top) + RowHeight;
            if (Mathf.Abs(topRect.height - target) < 0.5f) return;

            // Unity hard-codes the toolbar height and resets it on every relayout (resize, maximize, layout change).
            float delta = target - topRect.height;
            var centerRect = (Rect)position.GetValue(center);
            position.SetValue(top, new Rect(topRect.x, topRect.y, topRect.width, target));
            position.SetValue(center, new Rect(centerRect.x, centerRect.y + delta, centerRect.width, centerRect.height - delta));
            var field = s_MainViewType.GetField("m_TopViewHeight", k_Instance) ?? throw new Exception("MainView.m_TopViewHeight not found.");
            field.SetValue(mainView, target);
        }

        static void ResetLayout()
        {
            var mainView = MainView;
            if (mainView == null) return;
            var rect = Prop(s_ViewType, "position").GetValue(mainView);
            s_MainViewType.GetMethod("SetPosition", k_Instance)?.Invoke(mainView, new[] { rect });
        }

        static float BaseToolbarHeight(object toolbarView)
        {
            var calc = toolbarView.GetType().GetMethod("CalcHeight", k_Instance);
            return calc != null ? (float)calc.Invoke(toolbarView, null) : FallbackToolbarHeight;
        }

        static UnityEngine.Object MainView
        {
            get
            {
                if (s_MainView == null)
                {
                    if (s_MainViewType == null || s_ViewType == null) throw new Exception("UnityEditor.MainView not found.");
                    var found = Resources.FindObjectsOfTypeAll(s_MainViewType);
                    s_MainView = found.Length > 0 ? found[0] : null;
                }
                return s_MainView;
            }
        }

        static PropertyInfo Prop(Type type, string name) =>
            type.GetProperty(name, k_Instance) ?? throw new Exception($"{type.Name}.{name} not found.");

        #endregion

        #region Popups

        static void OpenMenu(string rootName, bool highlightFirst)
        {
            if (!s_RootButtons.TryGetValue(rootName, out var button) || Toolbar == null) { CloseAll(); return; }

            var node = DarkMenuNode.Build(rootName);
            var bounds = button.worldBound;
            if (node.Children.Count == 0)
            {
                // Nothing we can enumerate (unexpected): fall back to Unity's native popup for this menu.
                CloseAll();
                EditorUtility.DisplayPopupMenu(new Rect(bounds.x, bounds.yMax, 0, 0), rootName + "/", null);
                return;
            }

            var anchor = new Rect(Toolbar.position.x + bounds.x, Toolbar.position.y + bounds.y, bounds.width, bounds.height);
            s_LostFocusTicks = 0;
            // The previous popup's OnDestroy can fire while the new one is being shown; don't let it reset state.
            s_Opening = true;
            try
            {
                // Switching menus reuses the open root popup: closing/creating windows makes Windows hand
                // activation back to the main window, and Unity re-attaches its native menu (visible flicker).
                var existing = DarkMenuPopup.Chain.Find(w => w != null && w.Level == 0);
                DarkMenuPopup popup;
                if (existing != null) { existing.Retarget(node, anchor); popup = existing; }
                else popup = DarkMenuPopup.Open(node, anchor, 0);
                if (highlightFirst) popup.HighlightFirst();
            }
            finally { s_Opening = false; }
            OpenRoot = rootName;
            SetRootHighlight(rootName);
        }

        internal static void SwitchRoot(int direction)
        {
            var names = RootNames;
            int index = Array.IndexOf(names, OpenRoot);
            if (index < 0) return;
            var next = names[(index + direction + names.Length) % names.Length];
            Defer(() => OpenMenu(next, true));
        }

        internal static void CloseAll()
        {
            DarkMenuPopup.CloseFromLevel(0);
            OpenRoot = null;
            SetRootHighlight(null);
        }

        internal static void OnPopupsClosed()
        {
            if (s_Opening || DarkMenuPopup.Chain.Count > 0) return;
            OpenRoot = null;
            SetRootHighlight(null);
        }

        // Edit-menu commands (Copy, Paste, Duplicate, Select All, Frame Selected...) are routed to the focused
        // window. The native menu never takes focus; our popups do, so remember where the user was working.
        static EditorWindow s_WorkWindow;

        static void TrackWorkWindow()
        {
            var focused = EditorWindow.focusedWindow;
            if (focused == null || focused is DarkMenuPopup || focused.GetType() == s_ToolbarWindowType) return;
            s_WorkWindow = focused;
        }

        /// <summary>
        /// Run popup open/close work after the current UI event instead of inside it. Creating or closing popup
        /// windows mid-event makes Unity re-attach and paint its native menu bar for a few frames.
        /// </summary>
        internal static void Defer(Action action)
        {
            EditorApplication.delayCall += () =>
            {
                try { action(); }
                catch (Exception e) { Fail(e); }
            };
        }

        internal static void Execute(string menuPath)
        {
            var target = s_WorkWindow;
            Defer(() =>
            {
                CloseAll();
                if (target != null) target.Focus();
                EditorApplication.ExecuteMenuItem(menuPath);
            });
        }

        static void WatchPopupFocus()
        {
            if (DarkMenuPopup.Chain.Count == 0) return;
            bool focused = InternalEditorUtility.isApplicationActive && EditorWindow.focusedWindow is DarkMenuPopup;
            if (focused) { s_LostFocusTicks = 0; return; }
            if (++s_LostFocusTicks < 2) return; // focus can lag a tick behind opening a popup

            s_LastAutoClosedRoot = OpenRoot;
            s_LastAutoCloseTime = EditorApplication.timeSinceStartup;
            CloseAll();
        }

        #endregion

        #region Win32

        const uint MF_BYPOSITION = 0x400;
        const int GWLP_WNDPROC = -4;
        const uint WM_NCDESTROY = 0x0082;
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")] static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr GetMenu(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool SetMenu(IntPtr hWnd, IntPtr hMenu);
        [DllImport("user32.dll")] static extern bool IsMenu(IntPtr hMenu);
        [DllImport("user32.dll")] static extern bool DrawMenuBar(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr hMenu);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMenuStringW(IntPtr hMenu, uint id, StringBuilder s, int max, uint flags);

        #endregion
    }

    /// <summary>One entry of a Unity main menu, built from Menu.GetMenuItems (internal).</summary>
    class DarkMenuNode
    {
        public string Name;
        public string Path;
        public int Priority;
        public bool IsSubmenu;
        public readonly List<DarkMenuNode> Children = new List<DarkMenuNode>();
        public string Shortcut = "";
        public bool Enabled = true;
        public bool Checked;

        // Unity inserts a separator when consecutive items' priorities differ by 11 or more.
        public bool SeparatorBefore(int index) => index > 0 && (long)Children[index].Priority - Children[index - 1].Priority >= 11;

        static readonly MethodInfo s_GetMenuItems = typeof(Menu).GetMethod("GetMenuItems", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly MethodInfo s_GetEnabled = typeof(Menu).GetMethod("GetEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);

        public static DarkMenuNode Build(string rootName)
        {
            var root = new DarkMenuNode { Name = rootName, Path = rootName, IsSubmenu = true };
            if (s_GetMenuItems == null) throw new Exception("Menu.GetMenuItems not found.");

            var items = (Array)s_GetMenuItems.Invoke(null, new object[] { rootName, true, false });
            if (items == null) return root;
            var itemType = items.GetType().GetElementType();
            var pathProp = itemType.GetProperty("path", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var submenuProp = itemType.GetProperty("isSeparator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var priorityProp = itemType.GetProperty("priority", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var item in items)
            {
                var path = (string)pathProp.GetValue(item);
                if (string.IsNullOrEmpty(path) || !path.StartsWith(rootName + "/")) continue;
                int priority = (int)priorityProp.GetValue(item);
                // Menu.GetMenuItems flags submenu headers with isSeparator.
                bool header = submenuProp != null && (bool)submenuProp.GetValue(item);

                var parts = path.Substring(rootName.Length + 1).Split('/');
                var parent = root;
                for (int i = 0; i < parts.Length; i++)
                {
                    var child = parent.Children.Find(c => c.Name == parts[i]);
                    if (child == null)
                    {
                        child = new DarkMenuNode { Name = parts[i], Path = parent.Path + "/" + parts[i], Priority = priority };
                        parent.Children.Add(child);
                    }
                    if (i < parts.Length - 1 || header) child.IsSubmenu = true;
                    parent = child;
                }
            }
            return root;
        }

        /// <summary>Refresh shortcut/enabled/checked state of the direct children (validators run now).</summary>
        public void RefreshChildren()
        {
            foreach (var child in Children)
            {
                if (child.IsSubmenu) { child.Enabled = true; child.Shortcut = ""; continue; }
                try { child.Enabled = s_GetEnabled == null || (bool)s_GetEnabled.Invoke(null, new object[] { child.Path }); }
                catch { child.Enabled = true; }
                try { child.Checked = Menu.GetChecked(child.Path); } catch { child.Checked = false; }
                try { child.Shortcut = ShortcutManager.instance.GetShortcutBinding("Main Menu/" + child.Path).ToString(); }
                catch { child.Shortcut = ""; }
            }
        }
    }

    struct DarkMenuPalette
    {
        public Color Background, Border, Text, TextDim, Highlight, HighlightText, Separator, BarHover, BarOpen;

        public static DarkMenuPalette Current => EditorGUIUtility.isProSkin
            ? new DarkMenuPalette
            {
                Background = new Color32(40, 40, 40, 255),
                Border = new Color32(20, 20, 20, 255),
                Text = new Color32(210, 210, 210, 255),
                TextDim = new Color32(210, 210, 210, 130),
                Highlight = new Color32(44, 93, 135, 255),
                HighlightText = Color.white,
                Separator = new Color(1f, 1f, 1f, 0.09f),
                BarHover = new Color(1f, 1f, 1f, 0.08f),
                BarOpen = new Color32(44, 93, 135, 255),
            }
            : new DarkMenuPalette
            {
                Background = new Color32(242, 242, 242, 255),
                Border = new Color32(160, 160, 160, 255),
                Text = new Color32(20, 20, 20, 255),
                TextDim = new Color32(20, 20, 20, 140),
                Highlight = new Color32(58, 114, 176, 255),
                HighlightText = Color.white,
                Separator = new Color(0f, 0f, 0f, 0.12f),
                BarHover = new Color(0f, 0f, 0f, 0.08f),
                BarOpen = new Color32(58, 114, 176, 255),
            };
    }

    /// <summary>A borderless popup window showing one menu level. Submenus open as further popups.</summary>
    class DarkMenuPopup : EditorWindow
    {
        internal static readonly List<DarkMenuPopup> Chain = new List<DarkMenuPopup>();

        const float ItemHeight = 22f, SeparatorHeight = 9f, VerticalPadding = 4f;
        const float CheckColumn = 22f, RightPadding = 10f, ShortcutGap = 28f, ArrowColumn = 16f;

        DarkMenuNode m_Node;
        int m_Level;
        int m_Highlight = -1;
        readonly List<VisualElement> m_Rows = new List<VisualElement>();
        readonly List<DarkMenuNode> m_RowNodes = new List<DarkMenuNode>();
        IVisualElementScheduledItem m_HoverTimer;
        ScrollView m_Scroll;
        bool m_NeedsScroll; // menu taller than the screen: height was cut, allow vertical scrolling
        Rect m_Rect;        // intended screen rect (the position getter isn't reliable mid-show)
        const string DimClass = "borderless-menu-dim";

        internal static DarkMenuPopup Open(DarkMenuNode node, Rect anchor, int level)
        {
            node.RefreshChildren();
            var popup = CreateInstance<DarkMenuPopup>();
            popup.hideFlags = HideFlags.DontSave;
            popup.m_Node = node;
            popup.m_Level = level;

            var size = popup.CalcSize();
            var rect = Place(anchor, size, level > 0);
            popup.m_NeedsScroll = rect.height < size.y - 0.5f;
            popup.m_Rect = rect;
            popup.ShowPopup();
            popup.position = rect;
            popup.minSize = popup.maxSize = rect.size;
            Chain.Add(popup);
            popup.Focus();
            DarkMenuBar.HideNativeMenu();
            return popup;
        }

        internal int Level => m_Level;

        /// <summary>Show a different menu in this (root) popup without closing the window.</summary>
        internal void Retarget(DarkMenuNode node, Rect anchor)
        {
            CloseFromLevel(m_Level + 1);
            node.RefreshChildren();
            m_Node = node;
            var size = CalcSize();
            var rect = Place(anchor, size, m_Level > 0);
            m_NeedsScroll = rect.height < size.y - 0.5f;
            m_Rect = rect;
            minSize = Vector2.zero;
            maxSize = new Vector2(10000, 10000);
            position = rect;
            minSize = maxSize = rect.size;
            Rebuild();
        }

        internal static void CloseFromLevel(int level)
        {
            for (int i = Chain.Count - 1; i >= 0; i--)
            {
                var popup = Chain[i];
                if (popup == null) { Chain.RemoveAt(i); continue; }
                if (popup.m_Level < level) continue;
                Chain.RemoveAt(i);
                popup.Close();
            }
        }

        static Rect Place(Rect anchor, Vector2 size, bool submenu)
        {
            var bounds = EditorGUIUtility.GetMainWindowPosition();
            float x, y;
            if (submenu)
            {
                // anchor = parent popup's horizontal extent + highlighted row's vertical extent
                x = anchor.xMax - 2;
                if (x + size.x > bounds.xMax) x = anchor.x - size.x + 2;
                y = anchor.y - VerticalPadding - 1;
                if (y + size.y > bounds.yMax) y = bounds.yMax - size.y;
            }
            else
            {
                x = anchor.x;
                y = anchor.yMax;
                if (x + size.x > bounds.xMax) x = bounds.xMax - size.x;
            }
            y = Mathf.Max(y, bounds.y);
            size.y = Mathf.Min(size.y, bounds.yMax - y - 2);
            return new Rect(Mathf.Max(x, bounds.x), y, size.x, size.y);
        }

        Vector2 CalcSize()
        {
            var style = EditorStyles.label;
            float name = 0, shortcut = 0, height = VerticalPadding * 2 + 2;
            for (int i = 0; i < m_Node.Children.Count; i++)
            {
                var child = m_Node.Children[i];
                name = Mathf.Max(name, style.CalcSize(new GUIContent(child.Name)).x);
                if (!string.IsNullOrEmpty(child.Shortcut)) shortcut = Mathf.Max(shortcut, style.CalcSize(new GUIContent(child.Shortcut)).x);
                if (m_Node.SeparatorBefore(i)) height += SeparatorHeight;
                height += ItemHeight;
            }
            // Slack: text is measured with IMGUI but drawn by UI Toolkit, which can come out a pixel or two larger.
            float width = CheckColumn + name + (shortcut > 0 ? ShortcutGap + shortcut : 0) + ArrowColumn + RightPadding + 2 + 8;
            return new Vector2(Mathf.Ceil(Mathf.Max(width, 140)), Mathf.Ceil(height + 2));
        }

        void CreateGUI()
        {
            if (m_Node == null) { EditorApplication.delayCall += Close; return; } // stray instance after reload
            var p = DarkMenuPalette.Current;
            var root = rootVisualElement;
            root.style.backgroundColor = p.Background;
            root.style.borderTopWidth = root.style.borderBottomWidth = root.style.borderLeftWidth = root.style.borderRightWidth = 1;
            root.style.borderTopColor = root.style.borderBottomColor = root.style.borderLeftColor = root.style.borderRightColor = p.Border;
            root.focusable = true;
            root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            Rebuild();
            root.schedule.Execute(() => root.Focus());
        }

        void Rebuild()
        {
            var p = DarkMenuPalette.Current;
            var root = rootVisualElement;
            m_HoverTimer?.Pause();
            root.Clear();
            m_Rows.Clear();
            m_RowNodes.Clear();
            m_Highlight = -1;

            var scroll = m_Scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.verticalScrollerVisibility = m_NeedsScroll ? ScrollerVisibility.Auto : ScrollerVisibility.Hidden;
            scroll.contentContainer.style.paddingTop = scroll.contentContainer.style.paddingBottom = VerticalPadding;
            root.Add(scroll);

            for (int i = 0; i < m_Node.Children.Count; i++)
            {
                var child = m_Node.Children[i];
                if (m_Node.SeparatorBefore(i))
                {
                    var sep = new VisualElement();
                    sep.style.height = 1;
                    sep.style.marginTop = sep.style.marginBottom = (SeparatorHeight - 1) / 2;
                    sep.style.marginLeft = sep.style.marginRight = 6;
                    sep.style.backgroundColor = p.Separator;
                    scroll.Add(sep);
                }
                scroll.Add(BuildRow(child, m_Rows.Count, p));
                m_RowNodes.Add(child);
            }

            scroll.contentContainer.RegisterCallback<GeometryChangedEvent>(OnContentLaidOut);
        }

        // CalcSize measures with IMGUI, whose result varies with context (seen: 8px narrower when switching
        // menus). Once UI Toolkit has laid the rows out, measure them with the same text engine that draws
        // them and widen the window if something doesn't fit.
        void OnContentLaidOut(GeometryChangedEvent e)
        {
            ((VisualElement)e.target).UnregisterCallback<GeometryChangedEvent>(OnContentLaidOut);
            // Can fire while ShowPopup is still running (before Open sets the position): fit on the next tick.
            rootVisualElement.schedule.Execute(FitWidthToContent);
        }

        void FitWidthToContent()
        {
            if (m_Rows.Count == 0) return;

            float needed = 0;
            foreach (var row in m_Rows)
            {
                var rs = row.resolvedStyle;
                float width = rs.paddingLeft + rs.paddingRight + rs.marginLeft + rs.marginRight;
                foreach (var child in row.Children())
                {
                    if (!(child is Label label) || label.resolvedStyle.position == Position.Absolute) continue; // check mark
                    var ls = label.resolvedStyle;
                    float text = string.IsNullOrEmpty(label.text) ? 0 :
                        label.MeasureTextSize(label.text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
                    width += Mathf.Max(text + ls.paddingLeft + ls.paddingRight, ls.width) + ls.marginLeft + ls.marginRight;
                }
                needed = Mathf.Max(needed, width);
            }
            if (m_NeedsScroll) needed += 14; // vertical scrollbar
            needed = Mathf.Ceil(needed + 2 + 4); // window border + a little slack
            if (needed <= m_Rect.width + 0.5f) return;

            var rect = m_Rect;
            rect.width = needed;
            var bounds = EditorGUIUtility.GetMainWindowPosition();
            if (rect.xMax > bounds.xMax) rect.x = Mathf.Max(bounds.x, bounds.xMax - rect.width);
            minSize = Vector2.zero;
            maxSize = new Vector2(10000, 10000);
            position = rect;
            minSize = maxSize = rect.size;
            m_Rect = rect;
        }

        VisualElement BuildRow(DarkMenuNode node, int index, DarkMenuPalette p)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.height = ItemHeight;
            row.style.paddingLeft = CheckColumn;
            row.style.paddingRight = RightPadding;
            row.style.marginLeft = row.style.marginRight = 3;
            row.style.borderTopLeftRadius = row.style.borderTopRightRadius = row.style.borderBottomLeftRadius = row.style.borderBottomRightRadius = 3;
            if (!node.Enabled) row.style.opacity = 0.4f;

            if (node.Checked)
            {
                var check = new Label("✓");
                check.style.position = Position.Absolute;
                check.style.left = 5;
                check.style.color = p.Text;
                row.Add(check);
            }

            var label = new Label(node.Name);
            label.style.flexGrow = 1;
            label.style.flexShrink = 1;
            label.style.overflow = Overflow.Hidden;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.textOverflow = TextOverflow.Ellipsis;
            label.style.color = p.Text;
            label.style.paddingLeft = 0;
            row.Add(label);

            if (!string.IsNullOrEmpty(node.Shortcut))
            {
                var shortcut = new Label(node.Shortcut);
                shortcut.AddToClassList(DimClass);
                shortcut.style.color = p.TextDim;
                shortcut.style.marginLeft = ShortcutGap;
                shortcut.style.flexShrink = 0;
                row.Add(shortcut);
            }

            var arrow = new Label(node.IsSubmenu ? "›" : "");
            arrow.style.width = ArrowColumn;
            arrow.style.flexShrink = 0;
            arrow.style.unityTextAlign = TextAnchor.MiddleRight;
            arrow.style.color = p.Text;
            row.Add(arrow);

            row.RegisterCallback<PointerEnterEvent>(_ => SetHighlight(index, true));
            row.RegisterCallback<PointerUpEvent>(e => { if (e.button == 0) Activate(index, false); });
            m_Rows.Add(row);
            return row;
        }

        void SetHighlight(int index, bool fromMouse)
        {
            var p = DarkMenuPalette.Current;
            m_Highlight = index;
            for (int i = 0; i < m_Rows.Count; i++)
            {
                bool on = i == index && m_RowNodes[i].Enabled;
                m_Rows[i].style.backgroundColor = on ? new StyleColor(p.Highlight) : new StyleColor(StyleKeyword.Null);
                foreach (var label in m_Rows[i].Query<Label>().ToList())
                    label.style.color = on ? p.HighlightText : (label.ClassListContains(DimClass) ? p.TextDim : p.Text);
            }
            if (index >= 0 && index < m_Rows.Count) m_Scroll?.ScrollTo(m_Rows[index]);

            if (!fromMouse) return;
            // Like native menus: hovering opens a submenu (or closes a sibling's) after a short delay.
            m_HoverTimer?.Pause();
            m_HoverTimer = rootVisualElement.schedule.Execute(() =>
            {
                if (m_Highlight != index) return;
                if (m_RowNodes[index].IsSubmenu) OpenSubmenu(index, false);
                else CloseFromLevel(m_Level + 1);
            }).StartingIn(150);
        }

        internal void HighlightFirst() => Move(+1, -1);

        void Move(int direction, int from)
        {
            int count = m_Rows.Count;
            for (int step = 1; step <= count; step++)
            {
                int i = ((from + direction * step) % count + count) % count;
                if (m_RowNodes[i].Enabled) { SetHighlight(i, false); return; }
            }
        }

        void OpenSubmenu(int index, bool keyboard)
        {
            var node = m_RowNodes[index];
            CloseFromLevel(m_Level + 1);
            if (!node.IsSubmenu || node.Children.Count == 0) return;

            var row = m_Rows[index].worldBound;
            var anchor = new Rect(position.x, position.y + row.y, position.width, row.height);
            var child = Open(node, anchor, m_Level + 1);
            if (keyboard) child.HighlightFirst();
            else Focus(); // keep keyboard on this level while the mouse is exploring
        }

        void Activate(int index, bool keyboard)
        {
            if (index < 0 || index >= m_RowNodes.Count) return;
            var node = m_RowNodes[index];
            if (node.IsSubmenu) { OpenSubmenu(index, keyboard); return; }
            if (!node.Enabled) return;

            DarkMenuBar.Execute(node.Path);
        }

        void OnKeyDown(KeyDownEvent e)
        {
            switch (e.keyCode)
            {
                case KeyCode.DownArrow: Move(+1, m_Highlight); break;
                case KeyCode.UpArrow: Move(-1, m_Highlight < 0 ? 0 : m_Highlight); break;
                case KeyCode.RightArrow:
                    if (m_Highlight >= 0 && m_RowNodes[m_Highlight].IsSubmenu) OpenSubmenu(m_Highlight, true);
                    else DarkMenuBar.SwitchRoot(+1);
                    break;
                case KeyCode.LeftArrow:
                    if (m_Level > 0) CloseToParent();
                    else DarkMenuBar.SwitchRoot(-1);
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                case KeyCode.Space:
                    Activate(m_Highlight, true);
                    break;
                case KeyCode.Escape:
                    if (m_Level > 0) CloseToParent();
                    else DarkMenuBar.Defer(DarkMenuBar.CloseAll);
                    break;
                default: return;
            }
            e.StopPropagation();
        }

        void CloseToParent()
        {
            var parent = Chain.Find(w => w != null && w.m_Level == m_Level - 1);
            CloseFromLevel(m_Level);
            parent?.Focus();
        }

        void OnFocus() => DarkMenuBar.HideNativeMenu();
        void OnLostFocus() => DarkMenuBar.HideNativeMenu();

        void OnDestroy()
        {
            Chain.Remove(this);
            DarkMenuBar.OnPopupsClosed();
            DarkMenuBar.HideNativeMenu();
        }
    }
}
#endif
