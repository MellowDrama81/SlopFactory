using System.Runtime.InteropServices;

namespace Mellow.SlopFactory.Gui.Services;

/// <summary>
/// Real Windows notification-area icon (plan.md:441/444), implemented directly against the classic
/// Win32 Shell_NotifyIcon API rather than a third-party NuGet package — there is no notify-icon
/// control in the stable Windows App SDK, and this project prefers not to add a new dependency for
/// something achievable with a small amount of interop. A dedicated message-only-style native
/// window (not the MAUI main window) receives the icon's callback message and context-menu
/// commands; because it's created on the same UI thread WinUI already pumps messages on, no
/// separate message loop is needed here.
/// </summary>
internal sealed class WindowsTrayIconService : ITrayIconService, IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON_CALLBACK = WM_APP + 1;
    private const int WM_COMMAND = 0x0111;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_DESTROY = 0x0002;
    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int MENU_ID_OPEN = 1;
    private const int MENU_ID_EXIT = 2;
    private const int IDI_APPLICATION = 32512;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASS
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint LoadIcon(nint hInstance, nint lpIconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint hMenu, uint uFlags, nint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hWnd, nint lptpm);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private readonly WndProcDelegate _wndProc;
    private nint _hwnd;
    private bool _iconAdded;

    public WindowsTrayIconService()
    {
        _wndProc = WndProc;
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;

    public void Show(string tooltip)
    {
        EnsureWindow();
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON_CALLBACK,
            hIcon = LoadIcon(nint.Zero, IDI_APPLICATION),
            szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip
        };
        Shell_NotifyIcon(_iconAdded ? NIM_MODIFY : NIM_ADD, ref data);
        _iconAdded = true;
    }

    public void Hide()
    {
        if (!_iconAdded || _hwnd == nint.Zero) return;
        var data = new NOTIFYICONDATA { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1 };
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _iconAdded = false;
    }

    private void EnsureWindow()
    {
        if (_hwnd != nint.Zero) return;
        const string className = "SlopFactoryTrayIconWindow";
        var wndClass = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            lpszClassName = className
        };
        RegisterClass(ref wndClass);
        _hwnd = CreateWindowEx(0, className, className, 0, 0, 0, 0, 0, nint.Zero, nint.Zero, nint.Zero, nint.Zero);
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch ((int)msg)
        {
            case WM_TRAYICON_CALLBACK:
                switch ((int)lParam)
                {
                    case WM_LBUTTONDBLCLK:
                        OpenRequested?.Invoke(this, EventArgs.Empty);
                        break;
                    case WM_RBUTTONUP:
                        ShowContextMenu(hWnd);
                        break;
                }
                return nint.Zero;
            case WM_COMMAND:
                switch ((int)wParam & 0xFFFF)
                {
                    case MENU_ID_OPEN:
                        OpenRequested?.Invoke(this, EventArgs.Empty);
                        break;
                    case MENU_ID_EXIT:
                        ExitRequested?.Invoke(this, EventArgs.Empty);
                        break;
                }
                return nint.Zero;
            case WM_DESTROY:
                return nint.Zero;
            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    private static void ShowContextMenu(nint hWnd)
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, MENU_ID_OPEN, "Open SlopFactory");
            AppendMenu(menu, 0, MENU_ID_EXIT, "Exit");
            GetCursorPos(out var cursor);
            SetForegroundWindow(hWnd);
            TrackPopupMenuEx(menu, 0, cursor.X, cursor.Y, hWnd, nint.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        Hide();
        if (_hwnd != nint.Zero) { DestroyWindow(_hwnd); _hwnd = nint.Zero; }
    }
}
