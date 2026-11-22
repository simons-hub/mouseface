using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MouseFace
{
    // Every Windows API declaration in one place. The struct sizes are checked by the unit tests.
    internal static class Native
    {
        internal delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        internal delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        internal const int WH_MOUSE_LL = 14, WH_KEYBOARD_LL = 13;
        internal const int WM_QUIT = 0x0012, WM_INPUT_DEVICE_CHANGE = 0x00FE, WM_INPUT = 0x00FF;
        internal const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
            WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208,
            WM_MOUSEWHEEL = 0x020A, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C, WM_MOUSEHWHEEL = 0x020E;
        internal const int LLMHF_INJECTED = 0x1, LLKHF_INJECTED = 0x10;

        // MSLLHOOKSTRUCT: pt 0, mouseData 8, flags 12, time 16, then dwExtraInfo (pointer aligned).
        internal static readonly int MouseExtraOffset = IntPtr.Size == 8 ? 24 : 20;
        // KBDLLHOOKSTRUCT: vkCode 0, scanCode 4, flags 8, time 12, dwExtraInfo 16.
        internal const int KeyExtraOffset = 16;

        internal const uint RIDEV_INPUTSINK = 0x100, RIDEV_DEVNOTIFY = 0x2000;
        internal const uint RIDI_DEVICENAME = 0x20000007, RID_INPUT = 0x10000003;
        internal const int RIM_TYPEMOUSE = 0, GIDC_ARRIVAL = 1, GIDC_REMOVAL = 2;

        internal const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
            MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010,
            MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040,
            MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100, MOUSEEVENTF_HWHEEL = 0x1000;
        internal const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;

        internal const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
            WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
        internal const int OverlayStyles = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
        internal const int GWL_EXSTYLE = -20, GA_ROOT = 2, OBJID_WINDOW = 0;
        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_MESSAGE = new IntPtr(-3);
        internal const uint SWP_NOACTIVATE = 0x10, WDA_EXCLUDEFROMCAPTURE = 0x11;
        internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_OBJECT_SHOW = 0x8002;
        internal const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;

        internal const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        internal const int SM_SWAPBUTTON = 23;
        internal const uint MB_ICONINFORMATION = 0x40, MB_ICONWARNING = 0x30, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;

        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
        internal const int WM_DPICHANGED = 0x02E0;
        [StructLayout(LayoutKind.Sequential)]
        internal struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        internal struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct INPUT { public uint type; public INPUTUNION u; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct POWER_THROTTLING { public uint Version, ControlMask, StateMask; }

        // RAWMOUSE after the header: usFlags 0, usButtonFlags 4, usButtonData 6, ulRawButtons 8, lLastX 12, lLastY 16.
        internal static readonly int RawHeaderSize = Marshal.SizeOf(typeof(RAWINPUTHEADER));

        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] internal static extern int GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] internal static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] internal static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] internal static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] internal static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] internal static extern bool PostThreadMessage(int threadId, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] list, ref uint count, uint size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")] internal static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("user32.dll")] internal static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll")] internal static extern uint GetRawInputBuffer(IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] internal static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
        // Windows' own icons (the current ones, as its message boxes show), at any size: where each one
        // lives, then that icon drawn at the size asked for.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SHSTOCKICONINFO
        {
            internal uint cbSize;
            internal IntPtr hIcon;
            internal int iSysImageIndex, iIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string szPath;
        }
        internal const int SIID_HELP = 23, SIID_WARNING = 78, SIID_INFO = 79;
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern int SHGetStockIconInfo(int siid, uint flags, ref SHSTOCKICONINFO info);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern int SHDefExtractIcon(string file, int index, uint flags, out IntPtr large, out IntPtr small, uint sizes);
        // A shortcut (.lnk), through the shell's own object, in Unicode: any letter a path can have.
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        internal class ShellLink { }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        internal interface IShellLinkW
        {
            // All of them, in Windows' order, though only some are used: COM finds each by its place.
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int length, IntPtr findData, uint flags);
            void GetIDList(out IntPtr idList);
            void SetIDList(IntPtr idList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int length);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int length);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int length);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out ushort hotkey);
            void SetHotkey(ushort hotkey);
            void GetShowCmd(out int show);
            void SetShowCmd(int show);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int length, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string file, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] internal static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool GetUserObjectInformation(IntPtr obj, int index, StringBuilder info, uint length, out uint needed);
        [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc fn, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] internal static extern bool SetProcessInformation(IntPtr process, int infoClass, ref POWER_THROTTLING info, uint size);
        [DllImport("kernel32.dll")] internal static extern bool AllocConsole();
        [DllImport("kernel32.dll")] internal static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetStdHandle(int which);
        [DllImport("kernel32.dll")] internal static extern uint GetFileType(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        // Without MOVEFILE_COPY_ALLOWED it only moves within a drive and never falls back to a copy,
        // which would leave a running program's file where it was.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool MoveFileEx(string from, string to, uint flags);
        internal const uint MOVEFILE_REPLACE_EXISTING = 0x1;
        [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int needed);
        [DllImport("advapi32.dll")] internal static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
        [DllImport("advapi32.dll")] internal static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("hid.dll")] internal static extern bool HidD_GetProductString(IntPtr device, byte[] buffer, int length);
        [DllImport("hid.dll")] internal static extern bool HidD_GetManufacturerString(IntPtr device, byte[] buffer, int length);

        internal static bool KeyDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, string lParam);
    }
}
