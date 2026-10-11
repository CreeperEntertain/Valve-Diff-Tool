using Valve_Diff_Tool.Utilities;

namespace Valve_Diff_Tool.Services
{
    public sealed class BuildDiscoveryService
    {
        public List<string> DiscoverAndSort(string inputDirectory)
        {
            if (!Directory.Exists(inputDirectory))
                throw new DirectoryNotFoundException($"Input directory does not exist: {inputDirectory}");

            return Directory.GetDirectories(inputDirectory)
                .Select(BuildNameParser.Parse)
                .OrderBy(build => build.Date)
                .ThenBy(build => build.Time)
                .ThenBy(build => build.DepotId)
                .ThenBy(build => build.DepotVersion)
                .ThenBy(build => build.Name, StringComparer.Ordinal)
                .Select(build => build.FullPath)
                .ToList();
        }
    }
}
