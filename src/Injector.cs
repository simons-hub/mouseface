using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace MouseFace
{
    // Sends the replacement input on its own thread. The hook never sends input itself: SendInput
    // from inside a low-level hook stalls Windows' whole input pipeline.
    internal sealed class Injector
    {
        // Marks this program's own input, so its hook lets it pass.
        internal static readonly IntPtr Tag = new IntPtr(0x4D465250);

        readonly object gate = new object();
        readonly List<Out> queue = new List<Out>(32);
        readonly AutoResetEvent queued = new AutoResetEvent(false);
        volatile bool sending;
        volatile int sendStartedTick;

        internal void Start()
        {
            Thread t = new Thread(Run);
            t.IsBackground = true;
            t.Priority = ThreadPriority.Highest;
            t.Name = "injector";
            t.Start();
        }

        internal void Enqueue(List<Out> outs)
        {
            lock (gate) queue.AddRange(outs);
            queued.Set();
        }

        void Run()
        {
            List<Out> batch = new List<Out>(32);
            while (true)
            {
                queued.WaitOne();
                lock (gate)
                {
                    batch.Clear();
                    batch.AddRange(queue);
                    queue.Clear();
                    if (batch.Count > 0) MarkSending();
                }
                if (batch.Count > 0) Send(batch); // one call, so a double click's events stay together
                sending = false;
            }
        }

        // The start time is written before the busy flag, so a check that sees "busy" always reads
        // this send's start time, never an old one.
        internal void MarkSending()
        {
            sendStartedTick = Environment.TickCount;
            sending = true;
        }

        internal void MarkDone() { sending = false; }

        // SendInput can block for good when another program's hook hangs. If a send has not come back,
        // clicks would be swallowed and never replaced, so the program stops remapping. SendInput also
        // waits on every other program's mouse hook, each of which Windows allows up to a second, so
        // only a send still out after StuckMs counts.
        internal const int StuckMs = 2000;
        internal bool Stuck() { return sending && unchecked(Environment.TickCount - sendStartedTick) > StuckMs; }

        // Takes whatever is queued but not yet sent, for the crash handler to send itself.
        internal List<Out> TakeUnsent()
        {
            lock (gate) { List<Out> rest = new List<Out>(queue); queue.Clear(); return rest; }
        }

        internal bool Idle { get { lock (gate) return queue.Count == 0 && !sending; } }

        internal static void Send(List<Out> outs)
        {
            Native.INPUT[] inputs = Build(outs, Tag, Buttons.Swapped());
            Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        // SendInput names physical buttons, so with the buttons swapped in Windows a logical Left is
        // sent as the physical right button.
        internal static Native.INPUT[] Build(List<Out> outs, IntPtr tag, bool swapped)
        {
            Native.INPUT[] inputs = new Native.INPUT[outs.Count];
            for (int i = 0; i < outs.Count; i++)
            {
                Out o = outs[i];
                if (o.IsKey)
                {
                    inputs[i].type = Native.INPUT_KEYBOARD;
                    inputs[i].u.ki.wVk = o.Vk;
                    inputs[i].u.ki.wScan = (ushort)Native.MapVirtualKey(o.Vk, 0); // MAPVK_VK_TO_VSC
                    inputs[i].u.ki.dwFlags = (o.Down ? 0 : Native.KEYEVENTF_KEYUP) | (KeyNames.IsExtended(o.Vk) ? Native.KEYEVENTF_EXTENDEDKEY : 0);
                    inputs[i].u.ki.dwExtraInfo = tag;
                    continue;
                }
                uint flags = 0, data = 0;
                switch (Buttons.Physical(o.B, swapped))
                {
                    case Btn.Left: flags = o.Down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP; break;
                    case Btn.Right: flags = o.Down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP; break;
                    case Btn.Middle: flags = o.Down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP; break;
                    case Btn.Back: flags = o.Down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = 1; break;
                    case Btn.Forward: flags = o.Down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = 2; break;
                }
                inputs[i].type = Native.INPUT_MOUSE;
                inputs[i].u.mi.dwFlags = flags;
                inputs[i].u.mi.mouseData = data;
                inputs[i].u.mi.dwExtraInfo = tag;
            }
            return inputs;
        }
    }
}
