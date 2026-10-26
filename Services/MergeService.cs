namespace Valve_Diff_Tool.Services;

public sealed class MergeService
{
    private readonly HashService _hashService;

    public MergeService(HashService hashService)
    {
        _hashService = hashService;
    }

    public void MergeDirectory(
        string source,
        string destination,
        ISet<string>? completedFiles = null,
        Action<string>? onFileMerged = null)
    {
        string sourceRoot = Path.GetFullPath(source);
        string destinationRoot = Path.GetFullPath(destination);
        Directory.CreateDirectory(destinationRoot);

        MergeDirectoryCore(
            sourceRoot,
            destinationRoot,
            sourceRoot,
            completedFiles,
            onFileMerged);
    }

    private void MergeDirectoryCore(
        string source,
        string destination,
        string sourceRoot,
        ISet<string>? completedFiles,
        Action<string>? onFileMerged)
    {
        foreach (string sourceDirectory in Directory.GetDirectories(source).OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            string targetDirectory = Path.Combine(destination, Path.GetFileName(sourceDirectory));
            if (File.Exists(targetDirectory))
                throw new IOException($"Cannot merge directory into existing file: {targetDirectory}");

            Directory.CreateDirectory(targetDirectory);
            MergeDirectoryCore(
                sourceDirectory,
                targetDirectory,
                sourceRoot,
                completedFiles,
                onFileMerged);
        }

        foreach (string sourceFile in Directory.GetFiles(source).OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            string relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            if (completedFiles is not null && completedFiles.Contains(relativePath))
                continue;

            string destinationFile = Path.Combine(destination, Path.GetFileName(sourceFile));
            try
            {
                MergeFile(sourceFile, destinationFile);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Failed to merge file.{Environment.NewLine}" +
                    $"Relative path: {relativePath}{Environment.NewLine}" +
                    $"Source:        {sourceFile}{Environment.NewLine}" +
                    $"Destination:   {destinationFile}{Environment.NewLine}" +
                    $"Reason:        {exception.Message}",
                    exception);
            }

            onFileMerged?.Invoke(relativePath);
            completedFiles?.Add(relativePath);
        }
    }

    private void MergeFile(string sourceFile, string destinationFile)
    {
        string parent = Path.GetDirectoryName(destinationFile)!;
        Directory.CreateDirectory(parent);

        string extension = Path.GetExtension(destinationFile);
        string versionDirectory = destinationFile;
        string legacyVersionDirectory = Path.Combine(
            parent,
            Path.GetFileNameWithoutExtension(destinationFile));

        if (Directory.Exists(versionDirectory))
        {
            if (HasVersionFiles(versionDirectory, extension))
            {
                AddToExistingVersions(sourceFile, versionDirectory);
                return;
            }
            throw new IOException($"File conflicts with an existing directory: {destinationFile}");
        }

        if (Directory.Exists(legacyVersionDirectory) && HasVersionFiles(legacyVersionDirectory, extension))
        {
            if (File.Exists(destinationFile))
                throw new IOException($"Both the legacy version directory '{legacyVersionDirectory}' and the file '{destinationFile}' exist. Resolve this conflict manually.");

            Directory.Move(legacyVersionDirectory, versionDirectory);
            AddToExistingVersions(sourceFile, versionDirectory);
            return;
        }

        if (!File.Exists(destinationFile))
        {
            File.Copy(sourceFile, destinationFile);
            return;
        }

        if (_hashService.AreIdentical(sourceFile, destinationFile))
            return;

        string temporaryOriginal = Path.Combine(parent, $".{Path.GetFileName(destinationFile)}.{Guid.NewGuid():N}.tmp");

        File.Move(destinationFile, temporaryOriginal);

        try
        {
            Directory.CreateDirectory(versionDirectory);
            File.Move(temporaryOriginal, Path.Combine(versionDirectory, $"0{extension}"));
        }
        catch
        {
            if (File.Exists(temporaryOriginal))
            {
                if (Directory.Exists(versionDirectory) && Directory.GetFileSystemEntries(versionDirectory).Length == 0)
                    Directory.Delete(versionDirectory);
                if (!Directory.Exists(versionDirectory) && !File.Exists(destinationFile))
                    File.Move(temporaryOriginal, destinationFile);
            }
            throw;
        }

        int highestVersion = 0;
        foreach (string file in Directory.GetFiles(versionDirectory))
        {
            if (Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(Path.GetFileNameWithoutExtension(file), out int number))
                highestVersion = Math.Max(highestVersion, number);
        }

        string newVersion = Path.Combine(versionDirectory, $"{highestVersion + 1}{extension}");
        File.Copy(sourceFile, newVersion);
    }

    private static bool HasVersionFiles(string versionDirectory, string extension)
    {
        return
            Directory
                .GetFiles(versionDirectory)
                .Any(file => Path
                    .GetExtension(file)
                    .Equals(extension, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(Path.GetFileNameWithoutExtension(file), out _));
    }

    private void AddToExistingVersions(string sourceFile, string versionDirectory)
    {
        string extension = Path.GetExtension(sourceFile);

        var versions = Directory.GetFiles(versionDirectory)
            .Select(path => new
            {
                Path = path,
                Name = Path.GetFileNameWithoutExtension(path),
                Extension = Path.GetExtension(path)
            })
            .Where(file =>
                file.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(file.Name, out _))
            .Select(file => new
            {
                file.Path,
                Number = int.Parse(file.Name)
            })
            .OrderByDescending(file => file.Number)
            .ToList();

        if (versions.Count == 0)
            throw new IOException(
                $"No numbered versions found in '{versionDirectory}'. Cannot safely merge the incoming file.");

        foreach (var version in versions)
            if (_hashService.AreIdentical(sourceFile, version.Path))
                return;

        int nextNumber = versions[0].Number + 1;
        string nextVersion = Path.Combine(versionDirectory, $"{nextNumber}{extension}");
        File.Copy(sourceFile, nextVersion);
    }
}
