using System.Globalization;
using System.Text.Json;

namespace Valve_Diff_Tool.Services;

internal sealed class MetadataArchive
{
    public const string MetadataFileName = ".valve-diff-metadata.json";

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss";

    private readonly string _mergedRoot;
    private readonly string? _journalPath;
    private readonly HashService _hashService;
    private readonly StringComparer _pathComparer;

    private readonly Dictionary<string, FileHistory> _files;
    private readonly HashSet<string> _dirtyDirectories;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MetadataArchive(
        string mergedRoot,
        string? journalPath,
        HashService hashService)
    {
        _mergedRoot = Path.GetFullPath(mergedRoot);
        _journalPath = string.IsNullOrWhiteSpace(journalPath) ? null : Path.GetFullPath(journalPath);
        _hashService = hashService;
        _pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _files = new Dictionary<string, FileHistory>(_pathComparer);
        _dirtyDirectories = new HashSet<string>(_pathComparer);
        if (_journalPath is not null && File.Exists(_journalPath))
            LoadJournal(_journalPath);
    }

    public int TrackedFileCount
        => _files.Count;

    public bool HasEntries
        => _files.Count > 0;



    public static bool HasMetadataFiles(string mergedRoot)
        => Directory.Exists(mergedRoot) && Directory.EnumerateFiles(mergedRoot, MetadataFileName, SearchOption.AllDirectories).Any();

    public void Reset()
    {
        _files.Clear();
        _dirtyDirectories.Clear();
        if (_journalPath is null)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        File.WriteAllText(_journalPath, string.Empty);
    }

    public void ClearJournal()
    {
        if (_journalPath is not null && File.Exists(_journalPath))
            File.Delete(_journalPath);
    }

    public void RecordIncoming(
        string relativePath,
        string sourceFilePath,
        DateTime buildTimestamp,
        string buildName)
    {
        string normalizedPath = NormalizeRelativePath(relativePath);
        string parentPath = GetParentPath(normalizedPath);

        string sourceParent = Path.GetDirectoryName(sourceFilePath)!;
        string sourceParentName = Path.GetFileName(sourceParent);

        string directoryExtension = Path.GetExtension(sourceParentName);
        string incomingExtension = Path.GetExtension(sourceFilePath);
        string incomingBaseName = Path.GetFileNameWithoutExtension(sourceFilePath);

        int incomingVersion = -1;

        bool isNumberedVersion =
            parentPath.Length > 0 &&
            _pathComparer.Equals(
                GetFileName(parentPath),
                sourceParentName) &&
            incomingExtension.Equals(
                directoryExtension,
                StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(
                incomingBaseName,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out incomingVersion) &&
            HasNumberedVersionFiles(sourceParent, directoryExtension);

        if (isNumberedVersion)
        {
            string logicalPath = parentPath;
            string hash = _hashService.ComputeHash(sourceFilePath);
            int? explicitVersion = _files.ContainsKey(logicalPath) ? null : incomingVersion;

            RecordHash(logicalPath, hash, buildTimestamp, buildName, explicitVersion);

            return;
        }

        RecordHash(normalizedPath, _hashService.ComputeHash(sourceFilePath), buildTimestamp, buildName);
    }

    public void RecordHash(
        string relativePath,
        string sha256,
        DateTime buildTimestamp,
        string buildName,
        int? explicitVersion = null)
    {
        string key = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A metadata file path cannot be empty.", nameof(relativePath));
        if (_files.TryGetValue(key, out FileHistory? existing))
        {
            if (existing.Versions.Any(version =>
                    string.Equals(
                        version.Sha256,
                        sha256,
                        StringComparison.Ordinal)))
                return;
            if (explicitVersion.HasValue && existing.Versions.Any(version => version.Version == explicitVersion.Value))
                throw new InvalidDataException($"Two different hashes would occupy version {explicitVersion.Value} of '{key}'.");
        }

        var entry = new JournalEntry
        {
            Path = key,
            Sha256 = sha256,
            IntroducedAt = buildTimestamp.ToString(
                TimestampFormat,
                CultureInfo.InvariantCulture),
            Build = buildName,
            ExplicitVersion = explicitVersion
        };
        if (_journalPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
            File.AppendAllText(_journalPath, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }

        ApplyJournalEntry(entry);
    }

    public void TrackDirectory(
        string sourceRoot,
        DateTime buildTimestamp,
        string buildName)
    {
        string root = Path.GetFullPath(sourceRoot);
        TrackDirectoryCore(root, root, buildTimestamp, buildName);
    }

    private void TrackDirectoryCore(
        string currentDirectory,
        string sourceRoot,
        DateTime buildTimestamp,
        string buildName)
    {
        foreach (string sourceDirectory in Directory
                     .GetDirectories(currentDirectory)
                     .OrderBy(
                         path => Path.GetFileName(path),
                         StringComparer.OrdinalIgnoreCase))
            TrackDirectoryCore(sourceDirectory,sourceRoot,buildTimestamp,buildName);
        foreach (string sourceFile in Directory
                     .GetFiles(currentDirectory)
                     .OrderBy(
                         path => Path.GetFileName(path),
                         StringComparer.OrdinalIgnoreCase))
        {
            string relativePath = NormalizeRelativePath(Path.GetRelativePath(sourceRoot, sourceFile));
            RecordIncoming(relativePath, sourceFile, buildTimestamp, buildName);
        }
    }

    public void WriteDirtyMetadataFiles()
    {
        foreach (string relativeDirectory in _dirtyDirectories.ToArray())
        {
            var entries = _files
                .Where(pair => _pathComparer.Equals(
                    GetParentPath(pair.Key),
                    relativeDirectory))
                .OrderBy(
                    pair => GetFileName(pair.Key),
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (entries.Count == 0)
                continue;

            string directoryPath = relativeDirectory.Length == 0
                ? _mergedRoot
                : Path.Combine(_mergedRoot, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(directoryPath);

            var document = new MetadataDocument
            {
                SchemaVersion = 1,
                Files = new Dictionary<string, MetadataFile>(_pathComparer)
            };
            foreach (var pair in entries)
                document.Files[GetFileName(pair.Key)] = new MetadataFile
                {
                    Versions = pair.Value.Versions
                        .OrderBy(version => version.Version ?? -1)
                        .Select(version => new MetadataVersion
                        {
                            Version = version.Version,
                            Sha256 = version.Sha256,
                            IntroducedAt = version.IntroducedAt,
                            Build = version.Build
                        })
                        .ToList()
                };

            string destinationPath = Path.Combine(directoryPath, MetadataFileName);
            string temporaryPath = destinationPath + ".tmp";

            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, _jsonOptions));
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }

        _dirtyDirectories.Clear();
    }

    public void WriteAllAndPruneStaleFiles()
    {
        foreach (string parent in _files.Keys.Select(GetParentPath))
            _dirtyDirectories.Add(parent);

        WriteDirtyMetadataFiles();
        if (!Directory.Exists(_mergedRoot))
            return;

        var validParents = new HashSet<string>(_files.Keys.Select(GetParentPath), _pathComparer);
        foreach (string metadataPath in Directory
                     .EnumerateFiles(
                         _mergedRoot,
                         MetadataFileName,
                         SearchOption.AllDirectories)
                     .ToList())
        {
            string parent = NormalizeRelativePath(Path.GetRelativePath(_mergedRoot, Path.GetDirectoryName(metadataPath)!));
            if (!validParents.Contains(parent))
                File.Delete(metadataPath);
        }
    }

    public void ValidateAgainstMergedArchive()
    {
        if (!Directory.Exists(_mergedRoot))
            throw new DirectoryNotFoundException($"Merged archive does not exist: {_mergedRoot}");

        var actual = new Dictionary<string, List<PhysicalVersion>>(_pathComparer);
        var layoutErrors = new List<string>();

        ReadPhysicalDirectory(_mergedRoot, string.Empty, actual, layoutErrors);

        var errors = new List<string>(layoutErrors);

        foreach (var expectedPair in _files)
        {
            if (!actual.TryGetValue(
                    expectedPair.Key,
                    out List<PhysicalVersion>? actualVersions))
            {
                errors.Add($"Missing output path: {expectedPair.Key}");
                continue;
            }

            List<VersionRecord> expectedVersions = expectedPair.Value.Versions
                .OrderBy(version => version.Version ?? -1)
                .ToList();
            actualVersions = actualVersions
                .OrderBy(version => version.Version ?? -1)
                .ToList();

            if (expectedVersions.Count != actualVersions.Count)
            {
                errors.Add(
                    $"Version-count mismatch: {expectedPair.Key} " +
                    $"(expected {expectedVersions.Count}, " +
                    $"found {actualVersions.Count})");
                continue;
            }
            for (int i = 0; i < expectedVersions.Count; i++)
            {
                VersionRecord expected = expectedVersions[i];
                PhysicalVersion found = actualVersions[i];
                if (expected.Version != found.Version)
                    errors.Add(
                        $"Version-number mismatch: {expectedPair.Key} " +
                        $"(expected {expected.Version?.ToString() ?? "unversioned"}, " +
                        $"found {found.Version?.ToString() ?? "unversioned"})");
                if (!string.Equals(
                        expected.Sha256,
                        found.Sha256,
                        StringComparison.Ordinal))
                    errors.Add($"Hash mismatch: {expectedPair.Key} version {expected.Version?.ToString() ?? "unversioned"}");
            }
        }

        foreach (string unexpectedPath in actual.Keys.Where(path => !_files.ContainsKey(path)))
            errors.Add($"Unexpected output path: {unexpectedPath}");
        if (errors.Count == 0)
            return;

        string preview = string.Join(Environment.NewLine, errors.Take(12).Select(error => "  - " + error));
        if (errors.Count > 12)
            preview += Environment.NewLine + $"  - ... and {errors.Count - 12} more issue(s)";

        throw new InvalidDataException(
            "Metadata replay did not match the existing MERGED archive. " +
            "No metadata sidecars were written." +
            Environment.NewLine +
            preview +
            Environment.NewLine +
            "Check that the original builds and VPK extraction tool " +
            "match those used for the completed archive.");
    }

    private void LoadJournal(string path)
    {
        string[] lines = File.ReadAllLines(path);

        for (int i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;
            try
            {
                JournalEntry? entry = JsonSerializer.Deserialize<JournalEntry>(lines[i]);
                if (entry is not null &&
                    !string.IsNullOrWhiteSpace(entry.Path) &&
                    !string.IsNullOrWhiteSpace(entry.Sha256) &&
                    !string.IsNullOrWhiteSpace(entry.IntroducedAt))
                    ApplyJournalEntry(entry);
            }
            catch (JsonException) when (i == lines.Length - 1)
            {
                // Ignore partially written final journal line
            }
        }
    }

    private void ApplyJournalEntry(JournalEntry entry)
    {
        string path = NormalizeRelativePath(entry.Path);

        if (!_files.TryGetValue(path, out FileHistory? history))
        {
            history = new FileHistory();
            _files[path] = history;

            history.Versions.Add(new VersionRecord(
                entry.ExplicitVersion,
                entry.Sha256,
                entry.IntroducedAt,
                entry.Build));

            _dirtyDirectories.Add(GetParentPath(path));
            return;
        }

        if (history.Versions.Any(version => string.Equals(
                version.Sha256,
                entry.Sha256,
                StringComparison.Ordinal)))
            return;
        if (entry.ExplicitVersion.HasValue)
        {
            int requestedVersion = entry.ExplicitVersion.Value;
            if (history.Versions.Any(version => version.Version == requestedVersion))
                throw new InvalidDataException($"Two different hashes were assigned to version {requestedVersion} of '{path}'.");
            if (history.Versions.Count == 1 && history.Versions[0].Version is null)
            {
                if (requestedVersion == 0)
                    throw new InvalidDataException($"Cannot reconcile an unversioned file with incoming version 0 of '{path}'.");
                history.Versions[0] = history.Versions[0] with { Version = 0 };
            }

            history.Versions.Add(new VersionRecord(
                requestedVersion,
                entry.Sha256,
                entry.IntroducedAt,
                entry.Build));
        }
        else
        {
            if (history.Versions.Count == 1 && history.Versions[0].Version is null)
                history.Versions[0] = history.Versions[0] with { Version = 0 };

            int nextVersion = history.Versions
                .Where(version => version.Version.HasValue)
                .Select(version => version.Version!.Value)
                .DefaultIfEmpty(-1)
                .Max() + 1;

            history.Versions.Add(new VersionRecord(
                nextVersion,
                entry.Sha256,
                entry.IntroducedAt,
                entry.Build));
        }

        _dirtyDirectories.Add(GetParentPath(path));
    }

    private void ReadPhysicalDirectory(
        string currentDirectory,
        string relativeParent,
        Dictionary<string, List<PhysicalVersion>> result,
        List<string> layoutErrors)
    {
        foreach (string childDirectory in Directory
                     .GetDirectories(currentDirectory)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            string directoryName = Path.GetFileName(childDirectory);
            string extension = Path.GetExtension(directoryName);
            string logicalPath = CombineRelative(relativeParent, directoryName);
            var numberedFiles = GetNumberedVersionFiles(childDirectory, extension);
            bool isVersionDirectory = numberedFiles.Count > 0 && _files.ContainsKey(logicalPath);

            if (isVersionDirectory)
            {
                result[logicalPath] = numberedFiles
                    .Select(file => new PhysicalVersion(file.Version, _hashService.ComputeHash(file.Path)))
                    .ToList();

                var numberedPaths = new HashSet<string>(numberedFiles.Select(file => file.Path), _pathComparer);

                foreach (string file in Directory.GetFiles(childDirectory))
                    if (!numberedPaths.Contains(file))
                        layoutErrors.Add("Unexpected non-version file inside version directory: " + Path.GetRelativePath(_mergedRoot, file));
                if (Directory.GetDirectories(childDirectory).Length > 0)
                    layoutErrors.Add("Unexpected subdirectory inside version directory: " + Path.GetRelativePath(_mergedRoot, childDirectory));

                continue;
            }

            ReadPhysicalDirectory(childDirectory, logicalPath, result, layoutErrors);
        }

        foreach (string file in Directory
                     .GetFiles(currentDirectory)
                     .OrderBy(
                         path => Path.GetFileName(path),
                         StringComparer.OrdinalIgnoreCase))
        {
            string filename = Path.GetFileName(file);
            if (filename.Equals(MetadataFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (filename.Equals(MetadataFileName + ".tmp", StringComparison.OrdinalIgnoreCase))
            {
                layoutErrors.Add("Temporary metadata file left in output: " + Path.GetRelativePath(_mergedRoot, file));
                continue;
            }

            string relativePath = CombineRelative(relativeParent, filename);
            result[relativePath] = new List<PhysicalVersion> { new(null, _hashService.ComputeHash(file)) };
        }
    }

    private static bool HasNumberedVersionFiles(string directory, string extension)
        => GetNumberedVersionFiles(directory, extension).Count > 0;

    private static List<(string Path, int Version)> GetNumberedVersionFiles(string directory, string extension)
    {
        return Directory.GetFiles(directory)
            .Select(path => new
            {
                Path = path,
                Extension = Path.GetExtension(path),
                Name = Path.GetFileNameWithoutExtension(path)
            })
            .Where(file =>
                file.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(file.Name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(file => (file.Path, Version: int.Parse(file.Name, CultureInfo.InvariantCulture)))
            .OrderBy(file => file.Version)
            .ToList();
    }

    private static string NormalizeRelativePath(string path)
    {
        string normalized = path.Replace('\\', '/').TrimStart('/').TrimEnd('/');
        return normalized == "." ? string.Empty : normalized;
    }

    private static string GetParentPath(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static string GetFileName(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static string CombineRelative(string parent, string child)
        => parent.Length == 0 ? child : parent + "/" + child;



    private sealed class MetadataDocument
    {
        public int SchemaVersion { get; set; }
        public Dictionary<string, MetadataFile> Files { get; set; } = new();
    }

    private sealed class MetadataFile
    {
        public List<MetadataVersion> Versions { get; set; } = new();
    }

    private sealed class MetadataVersion
    {
        public int? Version { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string IntroducedAt { get; set; } = string.Empty;
        public string Build { get; set; } = string.Empty;
    }

    private sealed class JournalEntry
    {
        public string Path { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string IntroducedAt { get; set; } = string.Empty;
        public string Build { get; set; } = string.Empty;
        public int? ExplicitVersion { get; set; }
    }

    private sealed class FileHistory
    {
        public List<VersionRecord> Versions { get; } = new();
    }

    private sealed record VersionRecord(
        int? Version,
        string Sha256,
        string IntroducedAt,
        string Build);
    private sealed record PhysicalVersion(
        int? Version,
        string Sha256);
}
