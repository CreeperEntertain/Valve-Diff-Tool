using PortalBuildMerger.Services;
using Valve_Diff_Tool.Services;

namespace Valve_Diff_Tool
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            try
            {
                string inputDirectory = args.Length > 0
                    ? args[0]
                    : AskForPath("Input build archive directory");
                string outputDirectory = args.Length > 1
                    ? args[1]
                    : AskForPath("Output directory");
                string vpkEditCliPath = args.Length > 2
                    ? args[2]
                    : AskForPath("Path to vpkeditcli.exe");

                inputDirectory = Path.GetFullPath(inputDirectory);
                outputDirectory = Path.GetFullPath(outputDirectory);
                vpkEditCliPath = Path.GetFullPath(vpkEditCliPath);

                if (!Directory.Exists(inputDirectory))
                    throw new DirectoryNotFoundException(inputDirectory);
                if (string.IsNullOrWhiteSpace(outputDirectory))
                    throw new ArgumentException("Output directory is required.");
                if (!File.Exists(vpkEditCliPath))
                    throw new FileNotFoundException("VPKEdit CLI executable not found.", vpkEditCliPath);

                string mergedDirectory = Path.Combine(outputDirectory, "MERGED");
                string tempDirectory = Path.Combine(outputDirectory, "TEMP");

                if (IsSameOrNestedPath(outputDirectory, inputDirectory))
                    throw new InvalidOperationException("The output directory must not be inside the input directory.");

                var discovery = new BuildDiscoveryService();
                var directories = new DirectoryService();
                var extraction = new ArchiveExtractionService();
                var merger = new MergeService(new HashService());

                List<string> builds = discovery.DiscoverAndSort(inputDirectory);

                if (builds.Count == 0)
                {
                    Console.WriteLine("No build directories found.");
                    return 1;
                }

                string build = builds[0];

                Console.WriteLine($"Found {builds.Count} builds.");
                Console.WriteLine("TEST MODE: processing one build only.");
                Console.WriteLine($"Selected: {Path.GetFileName(build)}");

                Directory.CreateDirectory(mergedDirectory);
                Directory.CreateDirectory(tempDirectory);

                directories.ClearDirectory(tempDirectory);

                Console.WriteLine("Copying build subdirectories...");
                directories.CopySubdirectories(build, tempDirectory);

                Console.WriteLine("Extracting VPK archives...");
                await extraction.ExtractVpkArchivesAsync(build, tempDirectory, vpkEditCliPath);

                Console.WriteLine("Merging files...");
                merger.MergeDirectory(tempDirectory, mergedDirectory);

                directories.ClearDirectory(tempDirectory);

                Console.WriteLine("Test build merged successfully.");
                Console.WriteLine($"Merged archive: {mergedDirectory}");

                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"ERROR: {exception.Message}");
                return 1;
            }
        }

        private static string AskForPath(string prompt)
        {
            Console.Write($"{prompt}: ");
            string? value = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"{prompt} cannor be empty.");
            return value.Trim().Trim('"');
        }

        private static bool IsSameOrNestedPath(string candidate, string parent)
        {
            string normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            string normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));

            return
                normalizedCandidate.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase) ||
                normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
