using System;
using System.Drawing;
using System.IO;

namespace MouseFace
{
    internal static class App
    {
        internal const string Name = "MouseFace";
        internal const string Version = "1.0.0"; // also stamped on the .exe, through AssemblyInfo.cs
        internal const string Home = "https://github.com/simons-hub/mouseface";

        static string dataDir;
        static Icon icon;

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

        // Set by --watch so log lines show in the console too.
        internal static Action<string> Echo;

        internal static string FilePath { get { return Path.Combine(App.DataDir, App.Name + ".log"); } }

        internal static void Write(string line)
        {
            Action<string> echo = Echo;
            if (echo != null) echo("# " + line);
            lock (gate)
            {
                try
                {
                    string path = FilePath;
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxBytes) { File.Copy(path, path + ".old", true); File.Delete(path); }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
                }
                catch (Exception) { }
            }
        }
    }
}
