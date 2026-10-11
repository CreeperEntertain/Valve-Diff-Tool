using System.Diagnostics;

namespace PortalBuildMerger.Services;

public sealed class ArchiveExtractionService
{
    public async Task ExtractVpkArchivesAsync(
        string sourceBuildDirectory,
        string tempDirectory,
        string vpkEditCliPath,
        CancellationToken cancellationToken = default)
    {
        sourceBuildDirectory = Path.GetFullPath(sourceBuildDirectory);
        tempDirectory = Path.GetFullPath(tempDirectory);
        vpkEditCliPath = Path.GetFullPath(vpkEditCliPath);

        if (!Directory.Exists(sourceBuildDirectory))
            throw new DirectoryNotFoundException(sourceBuildDirectory);
        if (!File.Exists(vpkEditCliPath))
            throw new FileNotFoundException("VPKEdit CLI executable not found.", vpkEditCliPath);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        List<string> archives = Directory
            .EnumerateFiles(
                sourceBuildDirectory, "*.vpk",
                SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .ToList();

        var archiveSet = new HashSet<string>(archives, pathComparer);
        var chunks = new HashSet<string>(pathComparer);

        foreach (string file in archives)
        {
            if (!TryGetChunkBaseName(file, out string baseName))
                continue;

            string expectedDirectoryArchive =
                Path.Combine(
                    Path.GetDirectoryName(file)!,
                    $"{baseName}_dir.vpk");

            if (!archiveSet.Contains(expectedDirectoryArchive))
                throw new InvalidOperationException($"VPK chunk has no matching directory archive: {file}");

            chunks.Add(file);
        }

        List<string> archivesToExtract = archives
            .Where(file => !chunks.Contains(file))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = 0; i < archivesToExtract.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string archive = archivesToExtract[i];
            string relativeParent = Path.GetRelativePath(sourceBuildDirectory, Path.GetDirectoryName(archive)!);
            string destination = Path.GetFullPath(Path.Combine(tempDirectory, relativeParent));
            string tempRoot = Path.TrimEndingDirectorySeparator(tempDirectory) + Path.DirectorySeparatorChar;

            if (!destination.StartsWith(tempRoot, comparison) && !destination.Equals(tempDirectory, comparison))
                throw new InvalidOperationException($"Archive destination escapes TEMP: {archive}");

            Directory.CreateDirectory(destination);

            Console.WriteLine($"[{i + 1}/{archivesToExtract.Count}] Extracting {Path.GetFileName(archive)}");

            var startInfo = new ProcessStartInfo
            {
                FileName = vpkEditCliPath,
                WorkingDirectory = destination,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = false
            };

            startInfo.ArgumentList.Add(archive);
            startInfo.ArgumentList.Add("--extract");
            startInfo.ArgumentList.Add("/");
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(destination);

            using Process process = new() { StartInfo = startInfo };

            if (!process.Start())
                throw new InvalidOperationException($"Failed to start VPKEdit for {archive}");

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"VPKEdit failed on '{archive}' with exit code {process.ExitCode}.");
        }

        Console.WriteLine($"Extracted {archivesToExtract.Count} VPK archive(s).");
    }

    private static bool TryGetChunkBaseName(
        string path, out string baseName)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int separator = name.LastIndexOf('_');

        if (separator >= 0
            && name.Length - separator - 1 == 3
            && name[(separator + 1)..].All(char.IsDigit))
        {
            baseName = name[..separator];
            return true;
        }

        baseName = string.Empty;
        return false;
    }
}