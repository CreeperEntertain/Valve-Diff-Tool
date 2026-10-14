using System.Diagnostics;
using Valve_Diff_Tool.Services;

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

        var extractionMerger = new MergeService(new HashService());

        int processedArchives = 0;
        int warningCount = 0;

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
            string stagingDirectory = Path.Combine(Path.GetTempPath(), $"ValveDiffTool-vpkedit-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);

            try
            {
                WriteColoredLine($"[{i + 1}/{archivesToExtract.Count}] Extracting {Path.GetFileName(archive)}", ConsoleColor.Cyan);

                var startInfo = new ProcessStartInfo
                {
                    FileName = vpkEditCliPath,
                    WorkingDirectory = stagingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                startInfo.ArgumentList.Add(archive);
                startInfo.ArgumentList.Add("--extract");
                startInfo.ArgumentList.Add("/");
                startInfo.ArgumentList.Add("--output");
                startInfo.ArgumentList.Add(stagingDirectory);

                using Process process = new() { StartInfo = startInfo };

                if (!process.Start())
                    throw new InvalidOperationException($"Failed to start VPKEdit for {archive}");

                Task stdoutTask = ForwardOutputAsync(process.StandardOutput, Console.Out);
                Task stderrTask = ForwardOutputAsync(process.StandardError, Console.Error);

                try
                {
                    await process.WaitForExitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                    }

                    throw;
                }

                await Task.WhenAll(stdoutTask, stderrTask);

                if (process.ExitCode != 0)
                {
                    warningCount++;
                    WriteColoredLine(
                        $"Warning: VPKEdit failed on '{Path.GetFileName(archive)}' " +
                        $"with exit code {process.ExitCode}. " +
                        "Attempting to keep successfully extracted files.",
                        ConsoleColor.Yellow);
                }

                string[] stagedDirectories = Directory.GetDirectories(stagingDirectory);
                string[] stagedFiles = Directory.GetFiles(stagingDirectory);
                string extractionRoot;
                if (stagedDirectories.Length != 1 || stagedFiles.Length == 0)
                    extractionRoot = stagedDirectories[0];
                else
                {
                    warningCount++;
                    WriteColoredLine(
                        $"WARNING: Unexpected extraction layout for " +
                        $"'{Path.GetFileName(archive)}'. Attempting to preserve " +
                        "all staged output, including its current folder structure.",
                        ConsoleColor.Yellow);
                    extractionRoot = stagingDirectory;
                }
                if (Directory.GetFileSystemEntries(extractionRoot).Length == 0)
                {
                    warningCount++;
                    WriteColoredLine(
                        $"WARNING: No extracted files to preserve for " +
                        $"'{Path.GetFileName(archive)}'. Continuing.",
                        ConsoleColor.Yellow);
                    continue;
                }

                extractionMerger.MergeDirectory(stagedDirectories[0], destination);
                processedArchives++;
            }
            finally
            {
                await TryDeleteStagingDirectoryAsync(stagingDirectory);
            }
        }

        WriteColoredLine(
            $"Processed {processedArchives}/{archivesToExtract.Count} archive output(s); " +
            $"{warningCount} warning(s).",
            warningCount == 0 ? ConsoleColor.Green : ConsoleColor.Yellow);
    }

    private static bool TryGetChunkBaseName(string path, out string baseName)
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

    private static async Task ForwardOutputAsync(StreamReader reader, TextWriter destination)
    {
        char[] buffer = new char[1024];
        int count;

        while ((count = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await destination.WriteAsync(buffer, 0, count);
            await destination.FlushAsync();
        }
    }

    private static async Task TryDeleteStagingDirectoryAsync(string directory)
    {
        const int maxAttempts = 5;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(200 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                await Task.Delay(200 * attempt);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ConsoleColor previousColor = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine($"WARNING: Could not remove temporary staging directory '{directory}': {exception.Message}");
                }
                finally
                {
                    Console.ForegroundColor = previousColor;
                }
                return;
            }
    }

    private static void WriteColoredLine(string message, ConsoleColor color)
    {
        ConsoleColor previousColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
        }
        finally
        {
            Console.ForegroundColor = previousColor;
        }
    }
}