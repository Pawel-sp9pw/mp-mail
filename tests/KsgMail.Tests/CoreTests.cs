using System.Xml.Linq;
using KsgMail.Core;
using Xunit;

namespace KsgMail.Tests;

public sealed class CoreTests
{
    [Fact]
    public void MonthlyScheduleHonorsDayTimeAndDisabledFlag()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.Settings with { ScheduleEnabled = true, ScheduleDay = 12, ScheduleTime = "09:30" };
        Assert.False(settings.IsDue(new DateTime(2026, 10, 12, 9, 29, 0)));
        Assert.True(settings.IsDue(new DateTime(2026, 10, 12, 9, 30, 0)));
        Assert.True(settings.IsDue(new DateTime(2026, 10, 31, 23, 59, 0)));
        Assert.False((settings with { ScheduleEnabled = false }).IsDue(new DateTime(2026, 10, 12, 10, 0, 0)));
    }

    [Fact]
    public void WindowsTaskRunsDailyAndAtLogonWithScheduledSwitch()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.Settings with { ScheduleEnabled = true, ScheduleDay = 12, ScheduleTime = "07:45" };
        var xml = WindowsScheduler.BuildTaskXml(settings, @"C:\Program Files\MP Mail\MpMail.exe", "S-1-5-21-test");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal("2020-01-01T07:45:00", xml.Descendants(ns + "StartBoundary").Single().Value);
        Assert.Equal("1", xml.Descendants(ns + "DaysInterval").Single().Value);
        Assert.Single(xml.Descendants(ns + "LogonTrigger"));
        Assert.Equal("--scheduled", xml.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal(@"C:\Program Files\MP Mail\MpMail.exe", xml.Descendants(ns + "Command").Single().Value);
    }

    [Fact]
    public async Task ScheduledRunSendsOncePerCompanyPerMonthAndCatchesUpAfterDueTime()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Source, "file.txt"), "document");
        workspace.Repository.SaveCustomer(workspace.Customer);
        workspace.Repository.SaveSettings(workspace.Settings with { ScheduleEnabled = true, ScheduleDay = 12, ScheduleTime = "09:00" });
        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);

        await service.RunScheduledAsync(new DateTime(2026, 10, 12, 8, 59, 0), CancellationToken.None);
        Assert.Equal(0, sender.Count);
        await service.RunScheduledAsync(new DateTime(2026, 10, 12, 9, 0, 0), CancellationToken.None);
        Assert.Equal(1, sender.Count);
        await service.RunScheduledAsync(new DateTime(2026, 10, 20, 12, 0, 0), CancellationToken.None);
        Assert.Equal(1, sender.Count);
        await service.RunScheduledAsync(new DateTime(2026, 11, 20, 12, 0, 0), CancellationToken.None);
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
    public void CsvNewCompanyRequiresZipPassword()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "new.csv");
        File.WriteAllText(path,
            "id;nazwa;email;temat;tresc;katalog;haslo_zip;aktywny\n" +
            $";Nowa firma;new@example.test;Dokumenty;Treść;{workspace.Source};;1\n");
        Assert.Throws<UserException>(() => CustomerCsv.Import(path, [], workspace.Secrets));

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
