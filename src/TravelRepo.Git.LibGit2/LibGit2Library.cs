using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace TravelRepo.Git.LibGit2;

/// <summary>
/// Where libgit2 comes from on platforms where the system cannot find it by its usual name, such as iOS, where it ships
/// as a framework inside the app (<c>Frameworks/git2.framework/git2</c>, built by <c>eng/build-libgit2-ios.sh</c>).
/// </summary>
/// <remarks>
/// A class of its own: the first use of <see cref="LibGit2Backend"/> already calls into libgit2, so the path has to be
/// given before that class is touched.
/// </remarks>
public static class LibGit2Library
{
    /// <summary>The name LibGit2Sharp loads libgit2 by, which contains the libgit2 commit it was built for.</summary>
    public const string Name = "git2-5853918";

    private static readonly Lock Gate = new();
    private static string? used;

    /// <summary>
    /// Loads libgit2 from <paramref name="path"/> and uses it where the system cannot find it by <see cref="Name"/>.
    /// Call it once, before anything else of this package; calling it again with the same path does nothing.
    /// </summary>
    /// <exception cref="DllNotFoundException">The library cannot be loaded; the message says why.</exception>
    /// <exception cref="InvalidOperationException">libgit2 is already used from another path.</exception>
    public static void Use(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (Gate)
        {
            if (used is not null)
            {
                if (used == path) return;
                throw new InvalidOperationException("libgit2 is already used from " + used + ".");
            }
            var handle = NativeLibrary.Load(path);
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, name) =>
                name == Name && assembly.GetName().Name == "LibGit2Sharp" ? handle : IntPtr.Zero;
            used = path;
        }
    }
}
