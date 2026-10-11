using System.Globalization;
using Valve_Diff_Tool.Models;

namespace Valve_Diff_Tool.Utilities
{
    public static class BuildNameParser
    {
        public static BuildArchive Parse(string directoryPath)
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath));
            string[] parts = name.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 6)
                throw new FormatException($"Invalid build folder name: {name}");
            if (!int.TryParse(parts[0], out int depotId))
                throw new FormatException($"Invalid depot ID: {parts[0]}");
            if (!int.TryParse(parts[1], out int depotVersion))
                throw new FormatException($"Invalid depot version: {parts[1]}");

            DateOnly date = DateOnly.ParseExact(parts[2], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            TimeOnly time = TimeOnly.ParseExact(parts[3], "HH-mm-ss", CultureInfo.InvariantCulture);

            return new BuildArchive(
                Name: name,
                FullPath: directoryPath,
                DepotId: depotId,
                DepotVersion: depotVersion,
                Date: date,
                Time: time);
        }
    }
}
