namespace Valve_Diff_Tool.Services
{
    public sealed class MergeService
    {
        private readonly HashService _hashService;

        public MergeService(HashService hashService)
        {
            _hashService = hashService;
        }

        public void MergeDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (string sourceDirectory in Directory.GetDirectories(source))
            {
                string targetDirectory = Path.Combine(destination, Path.GetFileName(sourceDirectory));
                if (File.Exists(targetDirectory))
                    throw new IOException($"Cannot merge directory into existing file: {targetDirectory}");
                MergeDirectory(sourceDirectory, targetDirectory);
            }

            foreach (string sourceFile in Directory.GetFiles(source))
                MergeFile(sourceFile, Path.Combine(destination, Path.GetFileName(sourceFile)));
        }

        private void MergeFile(string sourceFile, string destinationFile)
        {
            string parent = Path.GetDirectoryName(destinationFile)!;
            Directory.CreateDirectory(parent);
            string versionDirectory = Path.Combine(parent, Path.GetFileNameWithoutExtension(destinationFile));

            if (Directory.Exists(destinationFile))
                throw new IOException($"File conflicts with an existing directory: {destinationFile}");
            if (Directory.Exists(versionDirectory))
            {
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

            string extension = Path.GetExtension(destinationFile);
            Directory.CreateDirectory(versionDirectory);
            string oldVersion = Path.Combine(versionDirectory, $"0{extension}");
            File.Move(destinationFile, oldVersion);
            int highestVersion = 0;

            foreach (string file in Directory.GetFiles(versionDirectory))
                if (Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase) && int.TryParse(Path.GetFileNameWithoutExtension(file), out int number))
                    highestVersion = Math.Max(highestVersion, number);

            string newVersion = Path.Combine(versionDirectory, $"{highestVersion + 1}{extension}");
            File.Copy(sourceFile, newVersion);
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
                    file.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(file.Name, out _))
                .Select(file => new
                {
                    file.Path,
                    Number = int.Parse(file.Name)
                })
                .OrderByDescending(file => file.Number)
                .ToList();

            if (versions.Count > 0)
            {
                string highestVersion = versions[0].Path;
                if (_hashService.AreIdentical(sourceFile, highestVersion))
                    return;

                int nextNumber = versions[0].Number + 1;
                string nextVersion = Path.Combine(versionDirectory, $"{nextNumber}{extension}");

                File.Copy(sourceFile, nextVersion);
                return;
            }

            throw new IOException(
                $"No numbered versions found in '{versionDirectory}'. " +
                "Cannot safely merge the incoming file.");
        }
    }
}
