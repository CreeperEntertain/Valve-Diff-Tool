namespace Valve_Diff_Tool.Services;

public sealed class DirectoryService
{
    public void CopySubdirectories(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.GetDirectories(source))
        {
            string target = Path.Combine(destination, Path.GetFileName(directory));
            CopyDirectoryContents(directory, target);
        }
    }

    private void CopyDirectoryContents(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            if (Path.GetExtension(file).Equals(".vpk", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (string directory in Directory.GetDirectories(source))
            CopyDirectoryContents(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    public void ClearDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (string entry in Directory.GetFileSystemEntries(directory))
            if (Directory.Exists(entry))
                Directory.Delete(entry, recursive: true);
            else
                File.Delete(entry);
    }
}
