using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Embedded;
using Microsoft.Extensions.Primitives;

namespace AiLog.Host;

/// <summary>
/// The UI files that dotnet publish embeds in the ailog executable, as a web root.
/// Resources are named "ui/" plus the file's web path, e.g. "ui/_ailog/index.html".
/// </summary>
internal sealed class EmbeddedUiFileProvider : IFileProvider
{
    public const string ResourcePrefix = "ui/";

    private readonly Assembly assembly;
    private readonly Dictionary<string, string> resourceNames;
    private readonly DateTimeOffset lastModified;

    private EmbeddedUiFileProvider(Assembly assembly, Dictionary<string, string> resourceNames)
    {
        this.assembly = assembly;
        this.resourceNames = resourceNames;
        lastModified = Environment.ProcessPath is { } executable ? File.GetLastWriteTimeUtc(executable) : DateTimeOffset.UtcNow;
    }

    /// <summary>Null when the assembly has no UI embedded, as in builds that are not published.</summary>
    public static EmbeddedUiFileProvider? Load(Assembly assembly)
    {
        Dictionary<string, string> resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))

            // Resource names keep the path separators of the machine that published them.
            .ToDictionary(name => name[ResourcePrefix.Length..].Replace('\\', '/'), StringComparer.Ordinal);

        return resourceNames.Count == 0 ? null : new EmbeddedUiFileProvider(assembly, resourceNames);
    }

    public IFileInfo GetFileInfo(string subpath)
    {
        string path = subpath.TrimStart('/');
        return resourceNames.TryGetValue(path, out string? resourceName)
            ? new EmbeddedResourceFileInfo(assembly, resourceName, Path.GetFileName(path), lastModified)
            : new NotFoundFileInfo(subpath);
    }

    /// <summary>Static files are served by exact path, so no directory is ever listed.</summary>
    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

    /// <summary>Embedded files never change while ailog runs.</summary>
    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
}
