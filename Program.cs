using PortalBuildMerger.Services;
using System.Text;
using System.Text.Json;
using Valve_Diff_Tool.Services;
using Valve_Diff_Tool.Utilities;

namespace Valve_Diff_Tool;

internal static class Program
{
    private const string PrepareStage = "Prepare";
    private const string MergeStage = "Merge";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            bool postHocMode = args.Any(arg => arg.Equals("--post", StringComparison.OrdinalIgnoreCase));
            string[] positionalArgs = args
                .Where(arg => !arg.Equals(
                    "--post",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            string inputDirectory = positionalArgs.Length > 0
                ? positionalArgs[0]
                : AskForPath("Input build archive directory");
            string outputDirectory = positionalArgs.Length > 1
                ? positionalArgs[1]
                : AskForPath("Output directory");
            string vpkEditCliPath = positionalArgs.Length > 2
                ? positionalArgs[2]
                : AskForPath("Path to vpkeditcli.exe");

            inputDirectory = Path.GetFullPath(inputDirectory);
            outputDirectory = Path.GetFullPath(outputDirectory);
            vpkEditCliPath = Path.GetFullPath(vpkEditCliPath);

            if (!Directory.Exists(inputDirectory))
                throw new DirectoryNotFoundException(inputDirectory);
            if (!File.Exists(vpkEditCliPath))
                throw new FileNotFoundException("VPKEdit CLI executable not found.", vpkEditCliPath);
            if (IsSameOrNestedPath(outputDirectory, inputDirectory))
                throw new InvalidOperationException("The output directory must not be inside the input directory.");

            string mergedDirectory = Path.Combine(outputDirectory, "MERGED");
            string tempDirectory = Path.Combine(outputDirectory, "TEMP");
            string checkpointPath = Path.Combine(outputDirectory, "progress.json");
            string mergeJournalPath = Path.Combine(outputDirectory, "merge-progress.jsonl");
            string metadataJournalPath = Path.Combine(outputDirectory, "metadata-progress.jsonl");

            var discovery = new BuildDiscoveryService();
            var directories = new DirectoryService();
            var extraction = new ArchiveExtractionService();
            var hashService = new HashService();
            var merger = new MergeService(new HashService());
            var metadata = new MetadataArchive(
                mergedDirectory,
                metadataJournalPath,
                hashService);

            List<string> builds = discovery.DiscoverAndSort(inputDirectory);
            if (builds.Count == 0)
            {
                WriteColoredLine("No build directories found.", ConsoleColor.Yellow);
                Console.ReadKey();
                return 1;
            }

            Directory.CreateDirectory(mergedDirectory);
            Directory.CreateDirectory(tempDirectory);

            if (postHocMode)
            {
                WriteColoredLine( "--post specified. Starting post-hoc metadata reconstruction.", ConsoleColor.Yellow);
                var replay = new MetadataReplayService();
                await replay.RebuildAsync(inputDirectory, mergedDirectory, vpkEditCliPath);
                WriteColoredLine("Post-hoc metadata reconstruction completed successfully.", ConsoleColor.Green);
                Console.ReadKey();
                return 0;
            }
            if (!File.Exists(checkpointPath) &&Directory.EnumerateFileSystemEntries(mergedDirectory).Any())
            {
                if (ConfirmPostHocReplay())
                {
                    WriteColoredLine("Switching to post-hoc metadata reconstruction.", ConsoleColor.Yellow);
                    var replay = new MetadataReplayService();
                    await replay.RebuildAsync(inputDirectory, mergedDirectory, vpkEditCliPath);
                    WriteColoredLine("Post-hoc metadata reconstruction completed successfully.", ConsoleColor.Green);
                    Console.ReadKey();
                    return 0;
                }
                WriteColoredLine("Continuing with the normal startup and save-state initialization.", ConsoleColor.Yellow);
            }

            ProgressState progress;
            if (File.Exists(checkpointPath))
            {
                progress = JsonSerializer.Deserialize<ProgressState>(File.ReadAllText(checkpointPath), JsonOptions)
                    ?? throw new InvalidDataException($"Checkpoint is empty or invalid: {checkpointPath}");
                if (!PathEquals(progress.InputDirectory, inputDirectory))
                    throw new InvalidOperationException(
                        $"The checkpoint belongs to a different input directory: {progress.InputDirectory}{Environment.NewLine}" +
                        $"Current input directory: {inputDirectory}{Environment.NewLine}" +
                        "Use the original input directory, or remove progress.json to start a new run.");

                bool mergeHasRecordedFiles = File.Exists(mergeJournalPath) && File.ReadLines(mergeJournalPath).Any(line => !string.IsNullOrWhiteSpace(line));
                if (!File.Exists(metadataJournalPath) && ((progress.BuildIndex > 0 && progress.BuildIndex < builds.Count) || (progress.Stage == MergeStage && mergeHasRecordedFiles)))
                    throw new InvalidOperationException(
                        "This archive was started without metadata tracking and is not marked complete. " +
                        "Resume it with its original save state before attempting metadata reconstruction.");
            }
            else
            {
                progress = new ProgressState
                {
                    InputDirectory = inputDirectory,
                    BuildIndex = 0,
                    ActiveBuildPath = builds[0],
                    Stage = PrepareStage
                };

                if (Directory.GetFileSystemEntries(tempDirectory).Length > 0 && ConfirmResumeExistingTemp())
                {
                    progress.Stage = MergeStage;
                    File.WriteAllText(mergeJournalPath, string.Empty);
                    WriteColoredLine("Existing TEMP will be merged into MERGED; it will not be cleared or re-extracted.", ConsoleColor.Yellow);
                }

                SaveProgress(checkpointPath, progress);
            }

            if (progress.BuildIndex < 0 || progress.BuildIndex > builds.Count)
                throw new InvalidDataException($"Invalid build index {progress.BuildIndex} in {checkpointPath}");

            if (progress.BuildIndex == builds.Count)
            {
                directories.ClearDirectory(tempDirectory);
                if (File.Exists(mergeJournalPath))
                    File.WriteAllText(mergeJournalPath, string.Empty);
                if (metadata.HasEntries)
                {
                    metadata.ValidateAgainstMergedArchive();
                    metadata.WriteAllAndPruneStaleFiles();
                    metadata.ClearJournal();
                }
                else if (!MetadataArchive.HasMetadataFiles(mergedDirectory))
                {
                    WriteColoredLine("The saved progress shows the archive is complete, but metadata is missing. Replaying the original builds into a virtual archive...", ConsoleColor.Yellow);
                    var replay = new MetadataReplayService();
                    await replay.RebuildAsync(inputDirectory, mergedDirectory, vpkEditCliPath);
                }
                else
                {
                    metadata.ClearJournal();
                    WriteColoredLine("The archive is already complete and metadata is present. No replay is necessary.", ConsoleColor.Green);
                }

                Console.WriteLine($"All {builds.Count} builds have been processed.");
                Console.WriteLine($"Checkpoint: {checkpointPath}");
                Console.ReadKey();
                return 0;
            }

            while (progress.BuildIndex < builds.Count)
            {
                string build = builds[progress.BuildIndex];
                if (!PathEquals(progress.ActiveBuildPath, build))
                {
                    throw new InvalidOperationException(
                        "The build order no longer matches progress.json. No data was cleared." + Environment.NewLine +
                        $"Checkpoint build: {progress.ActiveBuildPath}{Environment.NewLine}" +
                        $"Build at saved index {progress.BuildIndex}: {build}{Environment.NewLine}" +
                        "Restore the original input set/order, or remove progress.json to start over.");
                }
                if (progress.Stage != PrepareStage && progress.Stage != MergeStage)
                    throw new InvalidDataException($"Unknown checkpoint stage '{progress.Stage}' in {checkpointPath}");

                var parsedBuild = BuildNameParser.Parse(build);
                DateTime buildTimestamp = parsedBuild.Date.ToDateTime(parsedBuild.Time);

                Console.WriteLine($"Found {builds.Count} builds.");
                WriteColoredLine($"Build {progress.BuildIndex + 1}/{builds.Count}: {Path.GetFileName(build)}", ConsoleColor.Cyan);

                if (progress.Stage == PrepareStage)
                {
                    directories.ClearDirectory(tempDirectory);
                    File.WriteAllText(mergeJournalPath, string.Empty);

                    WriteColoredLine("Copying build subdirectories...", ConsoleColor.Yellow);
                    directories.CopySubdirectories(build, tempDirectory);

                    WriteColoredLine("Extracting VPK archives...", ConsoleColor.Yellow);
                    await extraction.ExtractVpkArchivesAsync(build, tempDirectory, vpkEditCliPath);

                    progress.Stage = MergeStage;
                    SaveProgress(checkpointPath, progress);
                }
                else
                    WriteColoredLine("Resuming at MERGE stage. TEMP is being preserved.", ConsoleColor.Yellow);

                HashSet<string> completedFiles = LoadCompletedFiles(mergeJournalPath);
                WriteColoredLine($"Resuming merge with {completedFiles.Count} file(s) already recorded as complete.", ConsoleColor.Yellow);

                using (var journalStream = new FileStream(
                    mergeJournalPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read))
                using (var journalWriter = new StreamWriter(journalStream, new UTF8Encoding(false)))
                {
                    merger.MergeDirectory(
                        tempDirectory,
                        mergedDirectory,
                        completedFiles,
                        relativePath =>
                        {
                            string sourceFile = Path.Combine(tempDirectory, relativePath);

                            metadata.RecordIncoming(relativePath, sourceFile, buildTimestamp, parsedBuild.Name);
                            journalWriter.WriteLine(JsonSerializer.Serialize(relativePath));
                            journalWriter.Flush();
                        });
                }

                metadata.WriteDirtyMetadataFiles();

                progress.BuildIndex++;
                progress.Stage = PrepareStage;
                progress.ActiveBuildPath = progress.BuildIndex < builds.Count
                    ? builds[progress.BuildIndex]
                    : null;
                SaveProgress(checkpointPath, progress);

                directories.ClearDirectory(tempDirectory);
                File.WriteAllText(mergeJournalPath, string.Empty);

                WriteColoredLine("Build merged successfully.", ConsoleColor.Green);
                Console.WriteLine($"Progress: {progress.BuildIndex}/{builds.Count} build(s) completed.");
                Console.WriteLine($"Checkpoint: {checkpointPath}");
                Console.WriteLine($"Merged archive: {mergedDirectory}");
            }

            metadata.WriteDirtyMetadataFiles();
            metadata.WriteAllAndPruneStaleFiles();
            metadata.ClearJournal();

            WriteColoredLine($"All {builds.Count} builds have been processed.", ConsoleColor.Green);
            Console.ReadKey();
            return 0;
        }
        catch (Exception exception)
        {
            string logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Valve-Diff-Tool");
            string logPath = Path.Combine(logDirectory, "error.log");

            try
            {
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(
                    logPath,
                    $"[{DateTimeOffset.Now:O}]{Environment.NewLine}" +
                    $"{exception}{Environment.NewLine}" +
                    $"{new string('-', 80)}{Environment.NewLine}");
            }
            catch (Exception loggingException)
            {
                Console.Error.WriteLine($"Could not write error log: {loggingException}");
            }

            ConsoleColor previousColor = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"ERROR: {exception}");
            }
            finally
            {
                Console.ForegroundColor = previousColor;
            }
            Console.Error.WriteLine($"Error log: {logPath}");
            Console.ReadKey();
            return 1;
        }
    }

    private static HashSet<string> LoadCompletedFiles(string journalPath)
    {
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var completed = new HashSet<string>(comparer);

        if (!File.Exists(journalPath))
            return completed;

        foreach (string line in File.ReadLines(journalPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                string? relativePath = JsonSerializer.Deserialize<string>(line);
                if (!string.IsNullOrWhiteSpace(relativePath))
                    completed.Add(relativePath);
            }
            catch (JsonException)
            {
                // Ignore a malformed/partial final journal line. We retire that shit.
            }
        }

        return completed;
    }

    private static void SaveProgress(string checkpointPath, ProgressState progress)
    {
        string temporaryPath = checkpointPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(progress, JsonOptions));
        File.Move(temporaryPath, checkpointPath, overwrite: true);
    }

    private static bool ConfirmPostHocReplay()
    {
        Console.WriteLine();
        WriteColoredLine("progress.json is missing, but MERGED already contains files.", ConsoleColor.Yellow);
        Console.WriteLine("Would you like to switch to post-hoc metadata reconstruction?");
        Console.Write("Replay the original builds virtually? [y/N]: ");

        string? answer = Console.ReadLine();
        return
            answer?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true ||
            answer?.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool ConfirmResumeExistingTemp()
    {
        Console.Write("TEMP already contains files but progress.json is missing. Resume merging TEMP? [Y/n]: ");
        string? answer = Console.ReadLine();
        return string.IsNullOrWhiteSpace(answer)
            || answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
            || answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string AskForPath(string prompt)
    {
        Console.Write($"{prompt}: ");
        string? value = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{prompt} cannot be empty.");
        return value.Trim().Trim('"');
    }

    private static bool PathEquals(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return false;

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return Path.GetFullPath(left).Equals(Path.GetFullPath(right), comparison);
    }

    private static bool IsSameOrNestedPath(string candidate, string parent)
    {
        string normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        string normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return normalizedCandidate.Equals(normalizedParent, comparison) ||
               normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, comparison);
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

    private sealed class ProgressState
    {
        public ProgressState() { }

        public string InputDirectory { get; set; } = string.Empty;
        public int BuildIndex { get; set; }
        public string? ActiveBuildPath { get; set; }
        public string Stage { get; set; } = PrepareStage;
    }
}
