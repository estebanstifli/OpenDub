namespace Dubbing.Infrastructure.Configuration;

public static class AppPaths
{
    public static string InstallRoot => AppContext.BaseDirectory;
    // A portable marker is opt-in; never write inside a signed macOS app or an AppImage mount.
    public static string PortableRoot => Environment.GetEnvironmentVariable("OPENDUB_PORTABLE_DIR") is { Length: > 0 } configured
        ? Path.GetFullPath(configured) : InstallRoot;
    public static bool IsPortable => Environment.GetEnvironmentVariable("OPENDUB_PORTABLE_DIR") is { Length: > 0 } || File.Exists(Path.Combine(InstallRoot, "portable.mode"));
    public static string DataRoot => IsPortable ? Path.Combine(PortableRoot, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenDub");
    public static string ProjectsRoot => IsPortable ? Path.Combine(PortableRoot, "projects") : Path.Combine(DataRoot, "projects");
    public static string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(DataRoot, path));
    public static string StorePath(string path)
    {
        string full = Path.GetFullPath(path);
        string inside = Path.GetRelativePath(PortableRoot, full);
        return inside != ".." && !inside.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(inside)
            ? Path.GetRelativePath(DataRoot, full) : path;
    }
}
