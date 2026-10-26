using PortalBuildMerger.Services;
using Valve_Diff_Tool.Utilities;

namespace Valve_Diff_Tool.Services;

internal sealed class MetadataReplayService
{
    public async Task RebuildAsync(
        string inputDirectory,
        string mergedDirectory,
        string vpkEditCliPath,
        CancellationToken cancellationToken = default)
    {
        inputDirectory = Path.GetFullPath(inputDirectory);
        mergedDirectory = Path.GetFullPath(mergedDirectory);
        vpkEditCliPath = Path.GetFullPath(vpkEditCliPath);

        if (!Directory.Exists(inputDirectory))
            throw new DirectoryNotFoundException(inputDirectory);
        if (!Directory.Exists(mergedDirectory))
            throw new DirectoryNotFoundException($"The completed MERGED archive was not found: {mergedDirectory}");
        if (!File.Exists(vpkEditCliPath))
            throw new FileNotFoundException("VPKEdit CLI executable not found.", vpkEditCliPath);

        List<string> builds = new BuildDiscoveryService().DiscoverAndSort(inputDirectory);
        if (builds.Count == 0)
            throw new InvalidOperationException($"No build directories found in '{inputDirectory}'.");

        string replayRoot = Path.Combine(Path.GetTempPath(), $"Valve-Diff-Tool-MetadataReplay-{Guid.NewGuid():N}");

        string tempDirectory = Path.Combine(replayRoot, "TEMP");

        Directory.CreateDirectory(tempDirectory);

        var directories = new DirectoryService();
        var extraction = new ArchiveExtractionService();
        var hashes = new HashService();

        string metadataJournalPath = Path.Combine(Path.GetDirectoryName(mergedDirectory)!, "metadata-progress.jsonl");

        var virtualArchive = new MetadataArchive(mergedDirectory, metadataJournalPath, hashes);

        virtualArchive.Reset();

        Console.WriteLine($"Metadata replay journal: {metadataJournalPath}");

        try
        {
            for (int index = 0; index < builds.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string buildPath = builds[index];
                var build = BuildNameParser.Parse(buildPath);

                DateTime timestamp = build.Date.ToDateTime(build.Time);

                Console.WriteLine($"Metadata replay {index + 1}/{builds.Count}: {build.Name}");

                directories.ClearDirectory(tempDirectory);
                directories.CopySubdirectories(buildPath, tempDirectory);

                await extraction.ExtractVpkArchivesAsync(buildPath, tempDirectory, vpkEditCliPath, cancellationToken);

                virtualArchive.TrackDirectory(tempDirectory, timestamp, build.Name);
                Console.WriteLine(
                    $"Tracked so far: {virtualArchive.TrackedFileCount:N0} logical paths, " +
                    $"{virtualArchive.TrackedVersionCount:N0} content-version records.");
                Console.WriteLine($"Tracked {virtualArchive.TrackedFileCount:N0} logical file path(s).");
            }
            if (virtualArchive.TrackedFileCount == 0)
                throw new InvalidDataException(
                    "Post-hoc replay tracked zero logical files across all builds. " +
                    "Metadata JSON files cannot be generated. " +
                    "Check TEMP extraction and the discovered build directories.");

            Console.WriteLine(
                $"Replay finished: {virtualArchive.TrackedFileCount:N0} logical paths, " +
                $"{virtualArchive.TrackedVersionCount:N0} content-version records.");
            Console.WriteLine("Validating reconstructed content hashes and paths against MERGED...");
            virtualArchive.ValidateAgainstMergedArchive();
            Console.WriteLine("Validation passed. Writing per-directory metadata JSON files...");
            virtualArchive.WriteAllAndPruneStaleFiles();

            int metadataFileCount = Directory
                .EnumerateFiles(
                    mergedDirectory,
                    MetadataArchive.MetadataFileName,
                    SearchOption.AllDirectories)
                .Count();
            if (metadataFileCount == 0)
                throw new IOException(
                    "Replay validation succeeded, but no metadata JSON files were found " +
                    $"under '{mergedDirectory}'. Check directory permissions and the " +
                    "metadata writer.");
            Console.WriteLine(
                $"Successfully wrote/found {metadataFileCount:N0} metadata JSON file(s) " +
                $"for {virtualArchive.TrackedFileCount:N0} logical paths.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(replayRoot))
                    Directory.Delete(replayRoot, recursive: true);
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine($"Warning: could not remove replay directory '{replayRoot}': " + exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                Console.Error.WriteLine($"Warning: could not remove replay directory '{replayRoot}': " + exception.Message);
            }
        }
    }
}
