using System.IO;
using System.Xml.Linq;
using KsgMail.Core;
using Xunit;

namespace KsgMail.Tests;

public sealed class CoreTests
{
    [Fact]
    public void DailyScheduleHonorsTimeAndDisabledFlag()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "09:30" };
        Assert.False(settings.IsDue(new DateTime(2026, 10, 12, 9, 29, 0)));
        Assert.True(settings.IsDue(new DateTime(2026, 10, 12, 9, 30, 0)));
        Assert.False(settings.IsDue(new DateTime(2026, 10, 13, 8, 59, 0)));
        Assert.True(settings.IsDue(new DateTime(2026, 10, 13, 9, 30, 0)));
        Assert.False((settings with { ScheduleEnabled = false }).IsDue(new DateTime(2026, 10, 12, 10, 0, 0)));
    }

    [Fact]
    public void WindowsTaskRunsDailyAndAtLogonWithScheduledSwitch()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "07:45" };
        var xml = WindowsScheduler.BuildTaskXml(settings, @"C:\Program Files\MP Mail\MpMail.exe", "S-1-5-21-test");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Contains("2020-01-01T07:45:00", xml.Descendants(ns + "StartBoundary").Select(element => element.Value));
        Assert.Contains("2020-01-01T00:00:00", xml.Descendants(ns + "StartBoundary").Select(element => element.Value));
        Assert.All(xml.Descendants(ns + "DaysInterval"), element => Assert.Equal("1", element.Value));
        Assert.Contains("PT15M", xml.Descendants(ns + "Interval").Select(element => element.Value));
        Assert.Single(xml.Descendants(ns + "LogonTrigger"));
        Assert.Equal("--scheduled", xml.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal(@"C:\Program Files\MP Mail\MpMail.exe", xml.Descendants(ns + "Command").Single().Value);
    }

    [Fact]
    public async Task ScheduledRunSendsOncePerCompanyPerDay()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-001.pdf"), "document");
        workspace.Repository.SaveCustomer(workspace.Customer);
        workspace.Repository.SaveSettings(workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "09:00" });
        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);

        await service.RunScheduledAsync(new DateTime(2026, 10, 12, 8, 59, 0), CancellationToken.None);
        Assert.Equal(0, sender.Count);
        await service.RunScheduledAsync(new DateTime(2026, 10, 12, 9, 0, 0), CancellationToken.None);
        Assert.Equal(1, sender.Count);
        Assert.False(File.Exists(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-001.pdf")));
        Assert.True(File.Exists(Path.Combine(workspace.Source, "archiwum", "2026-10-12", "orzeczenie-profilaktyczne-p1-001.pdf")));
        await service.RunScheduledAsync(new DateTime(2026, 10, 12, 12, 0, 0), CancellationToken.None);
        Assert.Equal(1, sender.Count);
        File.WriteAllText(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-002.pdf"), "next document");
        await service.RunScheduledAsync(new DateTime(2026, 10, 13, 8, 59, 0), CancellationToken.None);
        Assert.Equal(1, sender.Count);
        await service.RunScheduledAsync(new DateTime(2026, 10, 13, 9, 0, 0), CancellationToken.None);
        Assert.Equal(2, sender.Count);
        Assert.True(File.Exists(Path.Combine(workspace.Source, "archiwum", "2026-10-13", "orzeczenie-profilaktyczne-p1-002.pdf")));
    }

    [Fact]
    public void DiscoverUsesOnlyRootDirectoryAndSeparatesNegativeFiles()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-123.pdf"), "current");
        File.WriteAllText(Path.Combine(workspace.Source, "NEG.orzeczenie-profilaktyczne-p1-999.pdf"), "negative");
        File.WriteAllText(Path.Combine(workspace.Source, "inne-orzeczenie.pdf"), "other");
        File.WriteAllText(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p2-123.pdf"), "other");
        File.WriteAllText(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-123.txt"), "other");
        var archive = Path.Combine(workspace.Source, "archiwum", "2026-10-11");
        Directory.CreateDirectory(archive);
        File.WriteAllText(Path.Combine(archive, "orzeczenie-profilaktyczne-p1-old.pdf"), "old");
        var subdirectory = Path.Combine(workspace.Source, "podkatalog");
        Directory.CreateDirectory(subdirectory);
        File.WriteAllText(Path.Combine(subdirectory, "orzeczenie-profilaktyczne-p1-hidden.pdf"), "hidden");

        var service = new ArchiveService(workspace.Secrets);
        var files = service.Discover(workspace.Source, 10 * 1048576L);
        var negativeFiles = service.Discover(workspace.Source, 10 * 1048576L, true);

        var file = Assert.Single(files);
        Assert.Equal("orzeczenie-profilaktyczne-p1-123.pdf", file.RelativePath);
        var negative = Assert.Single(negativeFiles);
        Assert.Equal("NEG.orzeczenie-profilaktyczne-p1-999.pdf", negative.RelativePath);
    }

    [Fact]
    public void DiscoverRejectsDirectoryWithoutMatchingMedicalCertificates()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Source, "other.pdf"), "other");
        var service = new ArchiveService(workspace.Secrets);

        var exception = Assert.Throws<UserException>(() => service.Discover(workspace.Source, 10 * 1048576L));

        Assert.Contains("orzeczenie-profilaktyczne-p1*.pdf", exception.Message);
    }

    [Fact]
    public void ArchiveRetentionDeletesOnlyExpiredFilesAndEmptyDirectories()
    {
        using var workspace = new TestWorkspace();
        var archive = Path.Combine(workspace.Source, "archiwum");
        var oldDirectory = Path.Combine(archive, "2026-01-01");
        var freshDirectory = Path.Combine(archive, "2026-10-01");
        Directory.CreateDirectory(oldDirectory);
        Directory.CreateDirectory(freshDirectory);
        var oldFile = Path.Combine(oldDirectory, "old.pdf");
        var freshFile = Path.Combine(freshDirectory, "fresh.pdf");
        File.WriteAllText(oldFile, "old");
        File.WriteAllText(freshFile, "fresh");
        var now = new DateTime(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(oldFile, now.AddDays(-91));
        File.SetLastWriteTimeUtc(freshFile, now.AddDays(-30));

        var deleted = new ArchiveService(workspace.Secrets).PurgeExpired(workspace.Source, 90, now);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(oldFile));
        Assert.False(Directory.Exists(oldDirectory));
        Assert.True(File.Exists(freshFile));
    }

    [Fact]
    public void ArchivingRefreshesRetentionTimestamp()
    {
        using var workspace = new TestWorkspace();
        var source = Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-old.pdf");
        File.WriteAllText(source, "document");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddYears(-2));
        var info = new FileInfo(source);
        var file = new SourceFile(source, Path.GetFileName(source), info.Length, info.LastWriteTimeUtc);
        var before = DateTime.UtcNow.AddMinutes(-1);

        new ArchiveService(workspace.Secrets).ArchiveSent(workspace.Source, [file], "2026-10-16");

        var destination = Path.Combine(workspace.Source, "archiwum", "2026-10-16", Path.GetFileName(source));
        Assert.True(File.Exists(destination));
        Assert.True(File.GetLastWriteTimeUtc(destination) >= before);
    }

    [Fact]
    public async Task NegativeFilesSendOncePerConfiguredInterval()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Source, "NEG.orzeczenie-profilaktyczne-p1-001.pdf"), "negative");
        workspace.Repository.SaveCustomer(workspace.Customer);
        workspace.Repository.SaveSettings(workspace.Settings with
        {
            ScheduleEnabled = true,
            ScheduleTime = "23:00",
            NegativeIntervalMinutes = 15
        });
        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);

        await service.RunAutomationsAsync(new DateTime(2026, 10, 14, 9, 7, 0), CancellationToken.None);
        Assert.Equal(1, sender.Count);
        Assert.True(File.Exists(Path.Combine(workspace.Source, "archiwum", "NEG-2026-10-14-0900", "NEG.orzeczenie-profilaktyczne-p1-001.pdf")));

        File.WriteAllText(Path.Combine(workspace.Source, "NEG.orzeczenie-profilaktyczne-p1-002.pdf"), "negative2");
        await service.RunAutomationsAsync(new DateTime(2026, 10, 14, 9, 10, 0), CancellationToken.None);
        Assert.Equal(1, sender.Count);
        await service.RunAutomationsAsync(new DateTime(2026, 10, 14, 9, 16, 0), CancellationToken.None);
        Assert.Equal(2, sender.Count);
    }

    [Fact]
    public void CsvRoundTripPreservesFieldsButNeverExportsZipPassword()
    {
        using var workspace = new TestWorkspace();
        var customer = workspace.Customer with
        {
            Name = "Firma; z separatorem",
            Body = "Linia 1\nLinia 2; i cudzysłów \"test\"",
            Active = false
        };
        var path = Path.Combine(workspace.Root, "companies.csv");
        CustomerCsv.Export(path, [customer]);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("zip-secret", text);

        var imported = CustomerCsv.Import(path, [customer], workspace.Secrets);
        var result = Assert.Single(imported.Customers);
        Assert.Equal(customer.Id, result.Id);
        Assert.Equal(customer.Name, result.Name);
        Assert.Equal(customer.Body, result.Body);
        Assert.Equal(customer.ZipPasswordProtected, result.ZipPasswordProtected);
        Assert.False(result.Active);
        Assert.Equal(0, imported.Added);
        Assert.Equal(1, imported.Updated);
    }

    [Fact]
    public void CsvImportAcceptsCommaSeparatedHeaderAndQuotedCommas()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "companies-comma.csv");
        File.WriteAllText(path,
            "id,nazwa,email,temat,tresc,katalog,haslo_zip,aktywny\n" +
            $",Firma testowa,test@example.test,Dokumenty,\"Treść, z przecinkiem\",{workspace.Source},sekret,1\n");

        var result = CustomerCsv.Import(path, [], workspace.Secrets);

        var company = Assert.Single(result.Customers);
        Assert.Equal("Firma testowa", company.Name);
        Assert.Equal("Treść, z przecinkiem", company.Body);
        Assert.Equal("sekret", workspace.Secrets.Unprotect(company.ZipPasswordProtected));
        Assert.True(company.Active);
    }

    [Fact]
    public void CsvImportTreatsBlankActiveAsInactiveAndAllowsBlankEmail()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "inactive.csv");
        File.WriteAllText(path,
            "id;nazwa;email;temat;tresc;katalog;haslo_zip;aktywny\n" +
            $";Firma bez maila;;Dokumenty;Treść;{workspace.Source};sekret;\n");

        var result = CustomerCsv.Import(path, [], workspace.Secrets);

        var company = Assert.Single(result.Customers);
        Assert.False(company.Active);
        Assert.Equal("", company.Email);
    }

    [Fact]
    public void CsvImportUpdatesExistingCompanyByNameWhenIdAndEmailAreMissing()
    {
        using var workspace = new TestWorkspace();
        var existing = workspace.Customer with { Name = "Firma istniejąca", Email = "old@example.test", Active = true };
        var path = Path.Combine(workspace.Root, "update.csv");
        File.WriteAllText(path,
            "id;nazwa;email;temat;tresc;katalog;haslo_zip;aktywny\n" +
            $";Firma istniejąca;;Nowy temat;Nowa treść;{workspace.Source};;\n");

        var result = CustomerCsv.Import(path, [existing], workspace.Secrets);

        var company = Assert.Single(result.Customers);
        Assert.Equal(existing.Id, company.Id);
        Assert.Equal(existing.ZipPasswordProtected, company.ZipPasswordProtected);
        Assert.Equal("Nowy temat", company.Subject);
        Assert.False(company.Active);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
    }

    [Fact]
    public void CsvImportUpdatesExistingCompanyByEmailWhenIdIsMissing()
    {
        using var workspace = new TestWorkspace();
        var existing = workspace.Customer with { Name = "Stara nazwa", Email = "match@example.test" };
        var path = Path.Combine(workspace.Root, "update-email.csv");
        File.WriteAllText(path,
            "id;nazwa;email;temat;tresc;katalog;haslo_zip;aktywny\n" +
            $";Nowa nazwa;match@example.test;Temat;Treść;{workspace.Source};;1\n");

        var result = CustomerCsv.Import(path, [existing], workspace.Secrets);

        var company = Assert.Single(result.Customers);
        Assert.Equal(existing.Id, company.Id);
        Assert.Equal("Nowa nazwa", company.Name);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
    }

    [Fact]
    public void CsvNewCompanyRequiresZipPassword()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "new.csv");
        File.WriteAllText(path,
            "id;nazwa;email;temat;tresc;katalog;haslo_zip;aktywny\n" +
            $";Nowa firma;new@example.test;Dokumenty;Treść;{workspace.Source};;1\n");
        Assert.Throws<UserException>(() => { CustomerCsv.Import(path, [], workspace.Secrets); });

        File.WriteAllText(path,
            "id;nazwa;email;temat;tresc;katalog;haslo_zip;aktywny\n" +
            $";Nowa firma;new@example.test;Dokumenty;Treść;{workspace.Source};nowe-haslo;1\n");
        var result = CustomerCsv.Import(path, [], workspace.Secrets);
        var company = Assert.Single(result.Customers);
        Assert.Equal("nowe-haslo", workspace.Secrets.Unprotect(company.ZipPasswordProtected));
        Assert.Equal(1, result.Added);
    }

    private sealed class RecordingSender : IMailSender
    {
        public int Count { get; private set; }
        public Task<SendOutcome> SendAsync(AppSettings settings, PreparedMail mail, string messageId, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new SendOutcome(DeliveryStatus.Success, "accepted"));
        }
    }
}
