using System.Reflection;
using System.Runtime.Loader;

// F-42 spike: which APIs can a plugin use to (a) find a newly indexed MusicVideo item and
// (b) persist artist links on it, on this Emby server build?
//
// Dumps: ILibraryManager / ILibraryMonitor / BaseItem / MusicVideo / InternalItemsQuery surfaces.

var sysDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "emby-server", "system");
if (args.Length > 0) sysDir = args[0];
Console.WriteLine($"system dir: {sysDir}");

var alc = new AssemblyLoadContext("probe", isCollectible: false);
alc.Resolving += (ctx, name) =>
{
    var simple = name.Name + ".dll";
    foreach (var dir in new[] { Path.GetDirectoryName(typeof(object).Assembly.Location)!, sysDir })
    {
        var p = Path.Combine(dir, simple);
        if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
    }
    return null;
};
foreach (var dll in Directory.GetFiles(sysDir, "*.dll"))
{
    try { alc.LoadFromAssemblyPath(dll); } catch { }
}

IEnumerable<Type> Safe(Assembly a)
{
    try { return a.GetTypes(); }
    catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
    catch { return Array.Empty<Type>(); }
}

string Sig(MethodInfo m)
{
    try
    {
        var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
        return $"{m.ReturnType.Name} {m.Name}({ps})";
    }
    catch { return m.Name + "(?)"; }
}

string PSig(PropertyInfo p)
{
    try { return $"{p.PropertyType.Name} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "set; " : "")}}}"; }
    catch { return p.Name + " { ? }"; }
}

var controller = alc.LoadFromAssemblyPath(Path.Combine(sysDir, "MediaBrowser.Controller.dll"));
var model = alc.LoadFromAssemblyPath(Path.Combine(sysDir, "MediaBrowser.Model.dll"));
var types = Safe(controller).Concat(Safe(model)).ToList();

void DumpMethods(string typeName, string? pattern = null)
{
    var t = types.FirstOrDefault(x => x.FullName == typeName || x.Name == typeName);
    Console.WriteLine($"=== {typeName} {(t == null ? "NOT FOUND" : "")} ===");
    if (t == null) return;
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).OrderBy(m => m.Name))
    {
        if (pattern != null && !m.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)) continue;
        Console.WriteLine("  " + Sig(m));
    }
}

void DumpProps(string typeName)
{
    var t = types.FirstOrDefault(x => x.FullName == typeName || x.Name == typeName);
    Console.WriteLine($"=== props {typeName} {(t == null ? "NOT FOUND" : "")} ===");
    if (t == null) return;
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(p => p.Name))
        Console.WriteLine("  " + PSig(p));
}

DumpMethods("MediaBrowser.Controller.Library.ILibraryManager");
DumpMethods("MediaBrowser.Controller.Library.ILibraryMonitor");
DumpMethods("MediaBrowser.Controller.Entities.BaseItem", "Update");
DumpMethods("MediaBrowser.Controller.Entities.BaseItem", "Refresh");
DumpMethods("MediaBrowser.Controller.Entities.BaseItem", "Save");
DumpProps("MediaBrowser.Controller.Entities.Movies.MusicVideo");
DumpProps("MediaBrowser.Controller.Entities.Audio.Audio");
DumpProps("MediaBrowser.Controller.Entities.BaseItem");
DumpMethods("MediaBrowser.Controller.Entities.BaseItem", "CreateQuery");
DumpProps("MediaBrowser.Controller.Library.InternalItemsQuery");
DumpMethods("MediaBrowser.Controller.Library.InternalItemsQuery");
