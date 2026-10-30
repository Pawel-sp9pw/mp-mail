using ICSharpCode.SharpZipLib.Zip;
using System.IO.Enumeration;

namespace KsgMail.Core;

public sealed record PreparedMail(Customer Customer, string Period, string Subject, string Body,
    string ArchivePath, IReadOnlyList<SourceFile> Files, long ArchiveBytes, string? Error, bool Skipped)
{
    public string Name => Customer.Name;
    public string Email => Customer.Email;
    public string Status => Skipped ? "Pominięty: już wysłano / status niepewny" : Error ?? "Gotowy";
    public string ZipSize => ArchiveBytes == 0 ? "—" : $"{ArchiveBytes / 1048576d:F2} MB";
    public int FileCount => Files.Count;
    public bool Ready => Error == null && !Skipped;
}

public sealed class PreparedBatch(AppSettings settings, string directory) : IDisposable
{
    public AppSettings Settings { get; } = settings;
    public List<PreparedMail> Items { get; } = [];
    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

public sealed class ArchiveService(ISecretProtector secrets)
{
    public const string SentArchiveDirectoryName = "archiwum";
    public const string SendFilePrefix = "orzeczenie-profilaktyczne-p1";
    public const string NegativeFilePrefix = "NEG.orzeczenie-profilaktyczne-p1";

    private static IReadOnlyList<FileDispatchRule> LegacyRules(int negativeInterval = 15) =>
    [
        new() { Pattern = SendFilePrefix + "*.pdf", Enabled = true, Mode = FileDispatchMode.Scheduled },
        new() { Pattern = NegativeFilePrefix + "*.pdf", Enabled = true, Mode = FileDispatchMode.Interval, IntervalMinutes = negativeInterval }
    ];

    public bool HasMatchingFiles(string directory, bool negativeOnly = false)
    {
        var rules = LegacyRules();
        return HasMatchingFiles(directory, rules, rule =>
            negativeOnly ? rule.Mode == FileDispatchMode.Interval : rule.Mode == FileDispatchMode.Scheduled);
    }

    public IReadOnlyList<SourceFile> Discover(string directory, long maxSourceBytes, bool negativeOnly = false)
    {
        var rules = LegacyRules();
        try
        {
            return Discover(directory, maxSourceBytes, rules,
                rule => negativeOnly ? rule.Mode == FileDispatchMode.Interval : rule.Mode == FileDispatchMode.Scheduled,
                negativeOnly ? "NEG" : "zbiorcza");
        }
        catch (UserException exception) when (exception.Message.StartsWith("Brak plików pasujących", StringComparison.Ordinal))
        {
            var pattern = negativeOnly ? "NEG.orzeczenie-profilaktyczne-p1*.pdf" : "orzeczenie-profilaktyczne-p1*.pdf";
            throw new UserException($"Brak plików pasujących do wzorca {pattern} bezpośrednio w katalogu firmy. Wiadomość nie zostanie wysłana.");
        }
    }

    public bool HasMatchingFiles(string directory, IReadOnlyList<FileDispatchRule> rules,
        Func<FileDispatchRule, bool> includeRule)
    {
        if (!Directory.Exists(directory)) return false;
        return Directory.EnumerateFiles(directory).Any(path =>
        {
            var rule = MatchingRule(Path.GetFileName(path), rules);
            return rule != null && includeRule(rule);
        });
    }

    public IReadOnlyList<SourceFile> Discover(string directory, long maxSourceBytes,
        IReadOnlyList<FileDispatchRule> rules, Func<FileDispatchRule, bool> includeRule, string groupDescription)
    {
        if (!Directory.Exists(directory)) throw new UserException("Katalog źródłowy nie istnieje lub jest niedostępny.");
        var root = Path.GetFullPath(directory);
        RejectLink(root);
        var files = new List<SourceFile>();
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(root))
        {
            var fileName = Path.GetFileName(path);
            var rule = MatchingRule(fileName, rules);
            if (rule == null || !includeRule(rule)) continue;
            RejectLink(path);
            var info = new FileInfo(path);
            total = checked(total + info.Length);
            if (total > maxSourceBytes) throw new UserException("Pliki źródłowe przekraczają skonfigurowany limit.");
            files.Add(new SourceFile(path, fileName, info.Length, info.LastWriteTimeUtc));
            if (files.Count > 10000) throw new UserException("Katalog zawiera ponad 10 000 plików.");
        }
        return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList();
    }

    private static FileDispatchRule? MatchingRule(string fileName, IReadOnlyList<FileDispatchRule> rules)
    {
        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            if (FileSystemName.MatchesSimpleExpression(rule.Pattern, fileName, true))
                return rule;
        }
        return null;
    }

    public void ArchiveSent(string sourceDirectory, IReadOnlyList<SourceFile> files, string period)
    {
        if (files.Count == 0) return;
        var root = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var archiveRoot = Path.Combine(root, SentArchiveDirectoryName, period);
        Directory.CreateDirectory(archiveRoot);

        foreach (var file in files)
        {
            var source = Path.GetFullPath(file.FullPath);
            var rootPrefix = root + Path.DirectorySeparatorChar;
            if (!source.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new UserException("Nie można zarchiwizować pliku spoza katalogu firmy.");
            if (!File.Exists(source))
                throw new UserException($"Nie znaleziono pliku do archiwizacji: {file.RelativePath}");

            var relative = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.Combine(archiveRoot, relative);
            var destinationDirectory = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(destinationDirectory);
            destination = UniqueDestination(destination);
            File.Move(source, destination);
            File.SetLastWriteTimeUtc(destination, DateTime.UtcNow);
        }
    }

    public int PurgeExpired(string sourceDirectory, int retentionDays, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory)) return 0;
        var archiveRoot = Path.Combine(Path.GetFullPath(sourceDirectory), SentArchiveDirectoryName);
        if (!Directory.Exists(archiveRoot)) return 0;
        RejectLink(archiveRoot);

        var cutoff = utcNow.AddDays(-retentionDays);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(archiveRoot, "*", options))
        {
            if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;
            File.Delete(path);
            deleted++;
        }

        foreach (var directory in Directory.EnumerateDirectories(archiveRoot, "*", options)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        return deleted;
    }

    private static string UniqueDestination(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new UserException($"Nie można utworzyć unikalnej nazwy w archiwum dla pliku {Path.GetFileName(path)}.");
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UserException("Katalog zawiera dowiązanie lub plik chmurowy. Użyj zwykłego lokalnego katalogu.");
    }

    public void Create(string destination, IReadOnlyList<SourceFile> files, Customer customer,
        long maxArchiveBytes, CancellationToken cancellationToken)
    {
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipOutputStream(output) { Password = secrets.Unprotect(customer.ZipPasswordProtected), IsStreamOwner = false };
        zip.SetLevel(6);
        var buffer = new byte[81920];
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLink(file.FullPath);
            using var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != file.Length || File.GetLastWriteTimeUtc(file.FullPath) != file.LastWriteUtc)
                throw new UserException("Pliki zmieniły się podczas przygotowania. Przygotuj podgląd ponownie.");
            zip.PutNextEntry(new ZipEntry(file.RelativePath) { AESKeySize = 256, Size = file.Length, DateTime = file.LastWriteUtc });
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                zip.Write(buffer, 0, count);
                if (output.Position > maxArchiveBytes) throw new UserException("ZIP przekracza limit załącznika.");
            }
            zip.CloseEntry();
        }
        zip.Finish();
        output.Flush();
        if (output.Length > maxArchiveBytes) throw new UserException("ZIP przekracza limit załącznika.");
    }
}
