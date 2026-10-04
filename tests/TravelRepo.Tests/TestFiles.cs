namespace TravelRepo.Tests;

/// <summary>Removes temporary test directories on every platform.</summary>
internal static class TestFiles
{
    /// <summary>
    /// Deletes <paramref name="path"/> recursively. Git stores objects as read-only files, which Windows refuses to delete,
    /// and a Git process that just exited may still hold a handle for a moment.
    /// </summary>
    public static void Delete(string path)
    {
        for (var attempt = 1; Directory.Exists(path); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10) { Thread.Sleep(100 * attempt); }
        }
    }
}
