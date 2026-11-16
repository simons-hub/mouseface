using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MouseFace
{
    internal struct DeviceIds
    {
        internal bool Known;
        internal bool SigVendor; // the vendor ID is a Bluetooth SIG company ID, not a USB one
        internal ushort Vid, Pid;
        internal string Bus; // "USB", "Bluetooth" or "other"

        public override string ToString() { return Known ? Vid.ToString("X4") + ":" + Pid.ToString("X4") : "no ID"; }
    }

    internal static class DevicePath
    {
        // USB, and USB receivers: ...HID#VID_1A7C&PID_0191#...
        static readonly Regex UsbIds = new Regex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        // Bluetooth Classic: ..._VID&00021a7c_PID&0191...  Bluetooth Low Energy: ..._Dev_VID&021a7c_PID&0191...
        // The digits before the last four say who issued the vendor ID (USB-IF or the Bluetooth SIG).
        static readonly Regex BluetoothIds = new Regex(@"VID&([0-9A-F]{6,8})_PID&([0-9A-F]{4})", RegexOptions.IgnoreCase);

        internal static DeviceIds Parse(string path)
        {
            DeviceIds d = new DeviceIds();
            d.Bus = "other";
            if (string.IsNullOrEmpty(path)) return d;
            bool bluetooth = path.IndexOf("{00001124-", StringComparison.OrdinalIgnoreCase) >= 0  // HID profile
                || path.IndexOf("{00001812-", StringComparison.OrdinalIgnoreCase) >= 0;           // HID over GATT
            Match m = BluetoothIds.Match(path);
            if (m.Success)
            {
                string v = m.Groups[1].Value;
                d.SigVendor = Convert.ToInt32(v.Substring(0, v.Length - 4), 16) == 1; // 1: Bluetooth SIG, 2: USB-IF
                d.Vid = Convert.ToUInt16(v.Substring(v.Length - 4), 16);
                d.Pid = Convert.ToUInt16(m.Groups[2].Value, 16);
                d.Known = true;
                d.Bus = "Bluetooth";
                return d;
            }
            m = UsbIds.Match(path);
            if (m.Success)
            {
                d.Vid = Convert.ToUInt16(m.Groups[1].Value, 16);
                d.Pid = Convert.ToUInt16(m.Groups[2].Value, 16);
                d.Known = true;
                d.Bus = bluetooth ? "Bluetooth" : "USB";
                return d;
            }
            if (bluetooth) d.Bus = "Bluetooth";
            return d;
        }
    }

    internal sealed class MouseDevice
    {
        internal IntPtr Handle;
        internal string Path;
        internal DeviceIds Ids;
    }

    internal static class Devices
    {
        // Every pointing device Windows reports right now (mice, receivers, touchpads' mouse halves).
        internal static List<MouseDevice> Mice()
        {
            List<MouseDevice> mice = new List<MouseDevice>();
            uint size = (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICELIST));
            uint count = 0;
            if (Native.GetRawInputDeviceList(null, ref count, size) == uint.MaxValue) return null; // could not read the list
            if (count == 0) return mice;
            Native.RAWINPUTDEVICELIST[] list = new Native.RAWINPUTDEVICELIST[count + 8]; // room for one arriving meanwhile
            count = (uint)list.Length;
            uint got = Native.GetRawInputDeviceList(list, ref count, size);
            if (got == uint.MaxValue) return null; // could not read the list
            for (int i = 0; i < got; i++)
            {
                if (list[i].dwType != Native.RIM_TYPEMOUSE) continue;
                string path = PathOf(list[i].hDevice);
                if (path == null) continue;
                MouseDevice d = new MouseDevice();
                d.Handle = list[i].hDevice;
                d.Path = path;
                d.Ids = DevicePath.Parse(path);
                mice.Add(d);
            }
            return mice;
        }

        internal static string PathOf(IntPtr device)
        {
            if (device == IntPtr.Zero) return null;
            uint chars = 0;
            Native.GetRawInputDeviceInfo(device, Native.RIDI_DEVICENAME, null, ref chars);
            if (chars == 0) return null;
            StringBuilder name = new StringBuilder((int)chars + 1);
            if (Native.GetRawInputDeviceInfo(device, Native.RIDI_DEVICENAME, name, ref chars) == uint.MaxValue) return null;
            return name.ToString();
        }

        // A name for menus: what the mouse calls itself, then its IDs and how it connects. Reading the
        // name opens the device for no access at all, which Windows allows without admin rights.
        internal static string Describe(MouseDevice d)
        {
            string product = null;
            IntPtr h = Native.CreateFile(d.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); // share read+write, open existing
            if (h != new IntPtr(-1))
            {
                try
                {
                    string maker = HidString(h, true), name = HidString(h, false);
                    if (name != null) product = maker != null && !name.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? maker + " " + name : name;
                }
                finally { Native.CloseHandle(h); }
            }
            if (product == null) product = d.Ids.Bus == "other" ? "Pointing device" : "Mouse";
            return product + " (" + (d.Ids.Known ? d.Ids.ToString() + ", " : "") + d.Ids.Bus + ")";
        }

        // The product name alone, for menus: "Evoluent VerticalMouse 4", not its maker's company name
        // and ID numbers (Describe keeps those, for the log).
        internal static string Name(MouseDevice d)
        {
            IntPtr h = Native.CreateFile(d.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h != new IntPtr(-1))
            {
                try { string name = HidString(h, false); if (!string.IsNullOrEmpty(name)) return name; }
                finally { Native.CloseHandle(h); }
            }
            return d.Ids.Bus == "other" ? "Pointing device" : "Mouse";
        }

        static string HidString(IntPtr h, bool manufacturer)
        {
            byte[] buf = new byte[256];
            bool ok = manufacturer ? Native.HidD_GetManufacturerString(h, buf, buf.Length) : Native.HidD_GetProductString(h, buf, buf.Length);
            if (!ok) return null;
            string s = Encoding.Unicode.GetString(buf);
            int end = s.IndexOf('\0');
            if (end >= 0) s = s.Substring(0, end);
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }
    }
}
