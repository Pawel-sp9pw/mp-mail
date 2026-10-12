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
        Assert.Equal("2020-01-01T07:45:00", xml.Descendants(ns + "StartBoundary").Single().Value);
        Assert.Equal("1", xml.Descendants(ns + "DaysInterval").Single().Value);
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
    public void DiscoverAlwaysExcludesArchiveDirectory()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-123.pdf"), "current");
        File.WriteAllText(Path.Combine(workspace.Source, "inne-orzeczenie.pdf"), "other");
        File.WriteAllText(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p2-123.pdf"), "other");
        File.WriteAllText(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-123.txt"), "other");
        var archive = Path.Combine(workspace.Source, "archiwum", "2026-10-11");
        Directory.CreateDirectory(archive);
        File.WriteAllText(Path.Combine(archive, "orzeczenie-profilaktyczne-p1-old.pdf"), "old");
        var nestedArchive = Path.Combine(workspace.Source, "podkatalog", "ArChIwUm");
        Directory.CreateDirectory(nestedArchive);
        File.WriteAllText(Path.Combine(nestedArchive, "orzeczenie-profilaktyczne-p1-old2.pdf"), "old2");

        var files = new ArchiveService(workspace.Secrets).Discover(workspace.Source, 10 * 1048576L);

        var file = Assert.Single(files);
        Assert.Equal("orzeczenie-profilaktyczne-p1-123.pdf", file.RelativePath);
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
