using System.Reflection;
using System.Runtime.InteropServices;

// What Windows shows in the .exe's Properties > Details. The version comes from App.Version, so
// there is one place to change it.
[assembly: AssemblyTitle("MouseFace")]
[assembly: AssemblyDescription("Mouse button remapper and screen dimmer")]
[assembly: AssemblyProduct("MouseFace")]
[assembly: AssemblyCompany(MouseFace.App.Publisher)] // the publisher in Settings > Apps > Startup
[assembly: AssemblyCopyright("Copyright (c) 2026 MouseFace. MIT License.")]
[assembly: AssemblyVersion(MouseFace.App.Version + ".0")]
[assembly: AssemblyFileVersion(MouseFace.App.Version + ".0")]
[assembly: AssemblyInformationalVersion(MouseFace.App.Version)]
[assembly: ComVisible(false)]
