using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;

namespace MouseFace
{
    internal static class App
    {
        internal const string Name = "MouseFace";
        internal const string Version = "1.0.0"; // also stamped on the .exe, through AssemblyInfo.cs
        internal const string Home = "https://github.com/simons-hub/mouseface";
        internal const string Publisher = "ZebraTree Media"; // shown in Settings > Apps, Installed apps and Startup

        static string dataDir;
        static Icon icon;

        // This program's own file, as Windows started it. Not Application.ExecutablePath, which reads a
        // path through a URL and gets one with # or %20 in it wrong.
        internal static string ExePath { get { using (Process p = Process.GetCurrentProcess()) return p.MainModule.FileName; } }

        // The program's icon with all its sizes, for the windows; Windows picks the size it needs.
        internal static Icon Icon { get { if (icon == null) icon = LoadIcon(Name + ".ico", Size.Empty); return icon; } }

        // One of the .ico files built into the .exe (see install.ps1), at the size given, or with all
        // its sizes when the size is empty.
        internal static Icon LoadIcon(string resource, Size size)
        {
            using (Stream s = typeof(App).Assembly.GetManifestResourceStream(resource))
            {
                if (s == null) return (Icon)SystemIcons.Application.Clone(); // built without the icons
                return size.IsEmpty ? new Icon(s) : new Icon(s, size);
            }
        }

        // %LOCALAPPDATA%\MouseFace holds settings.ini, the log and the saved brightness. The tests and
        // --data-dir point it somewhere else.
        internal static string DataDir
        {
            get
            {
                if (dataDir == null) dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
                Directory.CreateDirectory(dataDir);
                return dataDir;
            }
            set { dataDir = value; }
        }
    }

    // A small text log, safe to call from any thread except the hook thread (which must never wait on
    // a disk): the hook thread hands its lines to the UI thread instead.
    internal static class Log
    {
        static readonly object gate = new object();
        const long MaxBytes = 256 * 1024;
        static int rotateFailedAt; // under gate; 0 when the last rotation worked

        // Set by --watch so log lines show in the console too.
        internal static Action<string> Echo;

        internal static string FilePath { get { return Path.Combine(App.DataDir, App.Name + ".log"); } }

        internal static void Write(string line)
        {
            Action<string> echo = Echo;
            if (echo != null) echo("# " + line);
            lock (gate)
            {
                string path;
                try { path = FilePath; } catch (Exception) { return; }
                // Rotating can fail (the log open in a viewer, a read-only .old); the line is written anyway,
                // and rotating waits a minute before trying again rather than copy the log at every line.
                if (unchecked(Environment.TickCount - rotateFailedAt) > 60000 || rotateFailedAt == 0)
                {
                    try
                    {
                        FileInfo fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > MaxBytes) { File.Copy(path, path + ".old", true); File.Delete(path); }
                        rotateFailedAt = 0;
                    }
                    catch (Exception) { rotateFailedAt = Environment.TickCount | 1; }
                }
                try { File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine); }
                catch (Exception) { }
            }
        }
    }
}
