using System.IO;
using System.Xml.Linq;
using KsgMail.Core;
using Xunit;

namespace KsgMail.Tests;

public sealed class CoreTests
{
    [Fact]
    public void EmployerFieldUsesTextBeforeFirstComma()
    {
        const string text = """
zatrudniony(-na)/przyjmowany(-na)*) do pracy w
Testowa firma, Mysłowicka 13, 40-486 Katowice
(nazwa i adres pracodawcy)
na stanowisku
""";

        Assert.Equal("Testowa firma", EmployerPdfValidator.ExtractEmployerNameFromText(text));
        Assert.True(EmployerPdfValidator.CompanyNamesEqual("  Testowa   firma ", "testowa firma"));
        Assert.False(EmployerPdfValidator.CompanyNamesEqual("Testowa firma", "Inna firma"));
    }

    [Fact]
    public void EmployerFieldStartsAfterDoPracyWAndIgnoresWrappedAddress()
    {
        const string text = """
zatrudniony(-na)/przyjmowany(-na)*) do pracy w
JSW S.A. KWK KNURÓW-SZCZYGŁOWICE-RUCH SZCZYGŁOWICE, 44-330 JASTRZĘBIE-ZDRóJ, AL. JANA PAWŁA
II 4
(nazwa i adres pracodawcy)
na stanowisku
""";

        Assert.Equal("JSW S.A. KWK KNURÓW-SZCZYGŁOWICE-RUCH SZCZYGŁOWICE",
            EmployerPdfValidator.ExtractEmployerNameFromText(text));
    }

    [Fact]
    public void EmployerMayStartOnSameLineAsDoPracyW()
    {
        const string text = """
zatrudniony do pracy w Testowa firma, Testowa 1, 00-001 Warszawa
(nazwa i adres pracodawcy)
""";

        Assert.Equal("Testowa firma", EmployerPdfValidator.ExtractEmployerNameFromText(text));
    }

    [Fact]
    public void EquivalentLegalFormsAreAccepted()
    {
        Assert.True(EmployerPdfValidator.CompanyNamesEqual(
            "ANPACARS SPÓŁKA Z OGRANICZONĄ ODPOWIEDZIALNOŚCIĄ",
            "ANPACARS SP. Z O.O."));

        Assert.True(EmployerPdfValidator.CompanyNamesEqual(
            "PRZYKŁAD SPÓŁKA AKCYJNA",
            "PRZYKŁAD S.A."));

        Assert.False(EmployerPdfValidator.CompanyNamesEqual(
            "ANPACARS SP. Z O.O.",
            "INNA FIRMA SP. Z O.O."));
    }

    [Fact]
    public async Task EmployerMismatchBlocksPackageBeforeSmtp()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-mismatch.pdf");
        TestPdf.WriteMedicalCertificate(path, "Inna firma");
        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);
        var settings = workspace.Settings with { ErrorNotificationEmail = "errors@example.test" };

        using var batch = await service.PrepareAsync(
            [workspace.Customer], settings, "2026-10", false, null, CancellationToken.None);

        var mail = Assert.Single(batch.Items);
        Assert.False(mail.Ready);
        Assert.Contains("nie zgadza się z firmą w bazie", mail.Error, StringComparison.CurrentCultureIgnoreCase);
        await service.SendAsync(batch, false, null, CancellationToken.None);
        Assert.Equal(0, sender.Count);
        Assert.Equal(1, sender.ErrorNotificationCount);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ManualRevalidationUsesFreshCompanyNameFromDatabase()
    {
        using var workspace = new TestWorkspace();
        var original = workspace.Customer with { Name = "ANPACARS STARA NAZWA" };
        workspace.Repository.SaveCustomer(original);

        var path = Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-refresh.pdf");
        TestPdf.WriteMedicalCertificate(path, "ANPACARS SP. Z O.O.");

        var stalePreviewCustomer = original;
        var updated = original with { Name = "ANPACARS SP. Z O.O." };
        workspace.Repository.SaveCustomer(updated);

        var current = Assert.Single(workspace.Repository.Customers().Where(customer => customer.Id == stalePreviewCustomer.Id));
        Assert.Equal("ANPACARS SP. Z O.O.", current.Name);

        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);

        using var verified = await service.PrepareAsync(
            [current], workspace.Settings, "2026-10", true, null, CancellationToken.None);

        var mail = Assert.Single(verified.Items);
        Assert.True(mail.Ready);
        Assert.Null(mail.Error);
    }

    [Fact]
    public async Task ManualResendBypassesHistoryBlockAndRevalidatesPdfEveryTime()
    {
        using var workspace = new TestWorkspace();
        var customer = workspace.Customer;
        workspace.Repository.SaveCustomer(customer);
        workspace.Repository.Finish(customer, "2026-10", DeliveryStatus.Success, "previously sent", "old-message");

        var path = Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-manual.pdf");
        TestPdf.WriteMedicalCertificate(path, "Inna firma");

        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);

        using (var blocked = await service.PrepareAsync(
                   [customer], workspace.Settings, "2026-10", false, null, CancellationToken.None))
        {
            var mail = Assert.Single(blocked.Items);
            Assert.True(mail.Skipped);
        }

        using (var revalidated = await service.PrepareAsync(
                   [customer], workspace.Settings, "2026-10", true, null, CancellationToken.None))
        {
            var mail = Assert.Single(revalidated.Items);
            Assert.False(mail.Skipped);
            Assert.False(mail.Ready);
            Assert.Contains("nie zgadza się z firmą w bazie", mail.Error, StringComparison.CurrentCultureIgnoreCase);
        }

        TestPdf.WriteMedicalCertificate(path, customer.Name);

        using var valid = await service.PrepareAsync(
            [customer], workspace.Settings, "2026-10", true, null, CancellationToken.None);

        var ready = Assert.Single(valid.Items);
        Assert.True(ready.Ready);
        Assert.False(ready.Skipped);
    }

    [Fact]
    public async Task MissingFolderInAutomationDoesNotRetrySendOrSpamAlertsEveryMinute()
    {
        using var workspace = new TestWorkspace();
        var missing = Path.Combine(workspace.Root, "missing-company-folder");
        var customer = workspace.Customer with { SourceDirectory = missing };
        workspace.Repository.SaveCustomer(customer);
        workspace.Repository.SaveSettings(workspace.Settings with
        {
            ScheduleEnabled = true,
            ScheduleTime = "09:00",
            FileRules =
            [
                new()
                {
                    Pattern = "orzeczenie-profilaktyczne-p1*.pdf",
                    Enabled = true,
                    Mode = FileDispatchMode.Interval,
                    IntervalMinutes = 1
                }
            ]
        });

        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);

        await service.RunAutomationsAsync(new DateTime(2026, 10, 30, 9, 0, 0), CancellationToken.None);
        await service.RunAutomationsAsync(new DateTime(2026, 10, 30, 9, 1, 0), CancellationToken.None);
        await service.RunAutomationsAsync(new DateTime(2026, 10, 30, 9, 2, 0), CancellationToken.None);

        Assert.Equal(0, sender.Count);
        Assert.Equal(0, sender.ErrorNotificationCount);

        var logs = workspace.Repository.Logs();
        var warning = Assert.Single(logs.Where(log =>
            log.CustomerName == customer.Name &&
            log.Status == DeliveryStatus.Skipped &&
            log.Detail.Contains("Katalog dokumentów nie istnieje", StringComparison.CurrentCultureIgnoreCase)));
        Assert.NotNull(warning);
        Assert.DoesNotContain(logs, log =>
            log.CustomerName == customer.Name && log.Status == DeliveryStatus.Failed);
    }

    [Fact]
    public async Task EmptyFolderIsWarningAndNeverSendsOrNotifiesInManualMode()
    {
        using var workspace = new TestWorkspace();
        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);
        var settings = workspace.Settings with { ErrorNotificationEmail = "errors@example.test" };

        using var batch = await service.PrepareAsync(
            [workspace.Customer], settings, "2026-10", true, null, CancellationToken.None);

        var mail = Assert.Single(batch.Items);
        Assert.True(mail.Skipped);
        Assert.False(mail.Ready);
        Assert.Contains("Katalog nie zawiera plików", mail.Status);

        var progress = new CaptureProgress();
        await service.SendAsync(batch, true, progress, CancellationToken.None, notifyErrors: false);

        Assert.Equal(0, sender.Count);
        Assert.Equal(0, sender.ErrorNotificationCount);
        Assert.Contains(progress.Messages, message => message.StartsWith("Sprawdzanie:", StringComparison.Ordinal));
        Assert.DoesNotContain(progress.Messages, message => message.StartsWith("Wysyłka:", StringComparison.Ordinal));
        Assert.Equal(DeliveryStatus.Skipped, Assert.Single(workspace.Repository.Logs()).Status);
    }

    [Fact]
    public async Task ManualValidationErrorDoesNotSendErrorNotificationButScheduledPathDoes()
    {
        using var manualWorkspace = new TestWorkspace();
        TestPdf.WriteMedicalCertificate(
            Path.Combine(manualWorkspace.Source, "orzeczenie-profilaktyczne-p1-bad.pdf"), "Inna firma");
        var manualSender = new RecordingSender();
        var manualService = new BatchService(manualWorkspace.Repository,
            new ArchiveService(manualWorkspace.Secrets), manualSender, manualWorkspace.Temp);
        var settings = manualWorkspace.Settings with { ErrorNotificationEmail = "errors@example.test" };

        using (var manualBatch = await manualService.PrepareAsync(
                   [manualWorkspace.Customer], settings, "2026-10", true, null, CancellationToken.None))
        {
            await manualService.SendAsync(manualBatch, true, null, CancellationToken.None, notifyErrors: false);
        }
        Assert.Equal(0, manualSender.ErrorNotificationCount);

        using var scheduledWorkspace = new TestWorkspace();
        TestPdf.WriteMedicalCertificate(
            Path.Combine(scheduledWorkspace.Source, "orzeczenie-profilaktyczne-p1-bad.pdf"), "Inna firma");
        var scheduledSender = new RecordingSender();
        var scheduledService = new BatchService(scheduledWorkspace.Repository,
            new ArchiveService(scheduledWorkspace.Secrets), scheduledSender, scheduledWorkspace.Temp);
        using var scheduledBatch = await scheduledService.PrepareAsync(
            [scheduledWorkspace.Customer], settings, "2026-10", false, null, CancellationToken.None);
        await scheduledService.SendAsync(scheduledBatch, false, null, CancellationToken.None);
        Assert.Equal(1, scheduledSender.ErrorNotificationCount);
    }

    [Fact]
    public async Task SuccessfulDeliveryStoresCertificateCountAndReportsExport()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-report.pdf");
        TestPdf.WriteMedicalCertificate(path, workspace.Customer.Name);
        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);

        using var batch = await service.PrepareAsync(
            [workspace.Customer], workspace.Settings, "2026-10", true, null, CancellationToken.None);
        var progress = new CaptureProgress();
        await service.SendAsync(batch, true, progress, CancellationToken.None, notifyErrors: false);

        Assert.Contains(progress.Messages, message => message.StartsWith("Sprawdzanie:", StringComparison.Ordinal));
        Assert.Contains(progress.Messages, message => message.StartsWith("Wysyłka:", StringComparison.Ordinal));

        var log = Assert.Single(workspace.Repository.Logs());
        Assert.Equal(DeliveryStatus.Success, log.Status);
        Assert.Equal(1, log.FileCount);

        var csv = Path.Combine(workspace.Root, "report.csv");
        var pdf = Path.Combine(workspace.Root, "report.pdf");
        DeliveryReportExporter.ExportCsv(csv, workspace.Customer.Name, workspace.Repository.Logs());
        DeliveryReportExporter.ExportPdf(pdf, workspace.Customer.Name, workspace.Repository.Logs());

        var csvText = File.ReadAllText(csv);
        Assert.Contains("liczba_orzeczen", csvText);
        Assert.Contains(";1;", csvText);
        Assert.True(new FileInfo(pdf).Length > 500);
    }

    [Fact]
    public void ErrorNotificationEmailIsOptionalButValidatedWhenProvided()
    {
        using var workspace = new TestWorkspace();
        Validation.Settings(workspace.Settings with { ErrorNotificationEmail = "" });
        Validation.Settings(workspace.Settings with { ErrorNotificationEmail = "errors@example.test" });
        Assert.Throws<UserException>(() =>
            Validation.Settings(workspace.Settings with { ErrorNotificationEmail = "bad-address" }));
    }

    [Fact]
    public void TemplateDateUsesActualSendDateWhileMonthUsesSelectedPeriod()
    {
        var customer = new Customer { Name = "Firma testowa" };
        var rendered = Templates.Render(
            "{klient}|{miesiac}|{data}", customer, "2026-10", new DateOnly(2026, 10, 22));

        Assert.Equal("Firma testowa|2026-10|2026-10-22", rendered);
    }

    [Fact]
    public void DailyScheduleHonorsTimeAndDisabledFlag()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "09:30" };
        Assert.False(settings.IsDue(new DateTime(2026, 10, 12, 9, 29, 0)));
        Assert.True(settings.IsDue(new DateTime(2026, 10, 12, 9, 30, 0)));
        Assert.True(settings.IsDue(new DateTime(2026, 10, 12, 9, 30, 59)));
        Assert.False(settings.IsDue(new DateTime(2026, 10, 12, 9, 31, 0)));
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
        Assert.Equal("false", xml.Descendants(ns + "StartWhenAvailable").Single().Value);
        Assert.Equal("--scheduled", xml.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal(@"C:\Program Files\MP Mail\MpMail.exe", xml.Descendants(ns + "Command").Single().Value);
    }

    [Fact]
    public async Task ScheduledRunSendsOncePerCompanyPerDay()
    {
        using var workspace = new TestWorkspace();
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-001.pdf"), workspace.Customer.Name);
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
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-002.pdf"), workspace.Customer.Name);
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
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-123.pdf"), workspace.Customer.Name);
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "NEG.orzeczenie-profilaktyczne-p1-999.pdf"), workspace.Customer.Name);
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "inne-orzeczenie.pdf"), workspace.Customer.Name);
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p2-123.pdf"), workspace.Customer.Name);
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
    public void DiscoverReturnsEmptyWhenNoMedicalCertificatesMatch()
    {
        using var workspace = new TestWorkspace();
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "other.pdf"), workspace.Customer.Name);
        var service = new ArchiveService(workspace.Secrets);

        Assert.Empty(service.Discover(workspace.Source, 10 * 1048576L));
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
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "NEG.orzeczenie-profilaktyczne-p1-001.pdf"), workspace.Customer.Name);
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

        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "NEG.orzeczenie-profilaktyczne-p1-002.pdf"), workspace.Customer.Name);
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
    public void CsvNewCompanyWithoutZipPasswordImportsAsInactive()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "new.csv");
        File.WriteAllText(path,
            "id;nazwa;email;temat;tresc;katalog;haslo_zip;aktywny\n" +
            $";Nowa firma;new@example.test;Dokumenty;Treść;{workspace.Source};;1\n");

        var result = CustomerCsv.Import(path, [], workspace.Secrets);

        var company = Assert.Single(result.Customers);
        Assert.False(company.Active);
        Assert.Equal("", company.ZipPasswordProtected);
        Assert.Equal(1, result.Added);
    }

    [Fact]
    public void CsvNewCompanyWithoutEmailImportsAsInactive()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "new-no-email.csv");
        File.WriteAllText(path,
            "id;nazwa;email;temat;tresc;katalog;haslo_zip;aktywny\n" +
            $";Nowa firma;;Dokumenty;Treść;{workspace.Source};nowe-haslo;1\n");

        var result = CustomerCsv.Import(path, [], workspace.Secrets);

        var company = Assert.Single(result.Customers);
        Assert.False(company.Active);
        Assert.Equal("nowe-haslo", workspace.Secrets.Unprotect(company.ZipPasswordProtected));
    }

    [Fact]
    public void CsvNewCompanyWithEmailAndZipPasswordCanStayActive()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "new-active.csv");
        File.WriteAllText(path,
            "id;nazwa;email;temat;tresc;katalog;haslo_zip;aktywny\n" +
            $";Nowa firma;new@example.test;Dokumenty;Treść;{workspace.Source};nowe-haslo;1\n");

        var result = CustomerCsv.Import(path, [], workspace.Secrets);

        var company = Assert.Single(result.Customers);
        Assert.True(company.Active);
        Assert.Equal("nowe-haslo", workspace.Secrets.Unprotect(company.ZipPasswordProtected));
    }

    [Fact]
    public void MultipleScheduleTimesAreSortedDeduplicatedAndStored()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "16:30; 09:00, 13:00 09:00" };
        Validation.Settings(settings);
        workspace.Repository.SaveSettings(settings);
        var saved = workspace.Repository.Settings();
        Assert.Equal(new[] { new TimeOnly(9, 0), new TimeOnly(13, 0), new TimeOnly(16, 30) }, saved.GetScheduleTimes());
        Assert.False(saved.IsDue(new DateTime(2026, 10, 16, 8, 59, 0)));
        Assert.False(saved.IsDue(new DateTime(2026, 10, 16, 13, 1, 0)));
        Assert.Equal(new DateTime(2026, 10, 16, 9, 0, 0), saved.DueAt(new DateTime(2026, 10, 16, 12, 59, 0)));
        Assert.Equal(new DateTime(2026, 10, 16, 13, 0, 0), saved.DueAt(new DateTime(2026, 10, 16, 13, 0, 0)));
        Assert.Equal(new DateTime(2026, 10, 16, 16, 30, 0), saved.DueAt(new DateTime(2026, 10, 16, 23, 59, 0)));
        Assert.False(saved.IsDue(new DateTime(2026, 10, 17, 0, 0, 0)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" , ; ")]
    [InlineData("09:00, 24:00")]
    [InlineData("09:00, 9:30")]
    [InlineData("09:00, 13:60")]
    public void InvalidScheduleTimesAreRejected(string times)
    {
        using var workspace = new TestWorkspace();
        Assert.Throws<UserException>(() => Validation.Settings(workspace.Settings with { ScheduleTime = times }));
    }

    [Fact]
    public void ExistingSingleTimeSettingsRemainCompatible()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"ScheduleEnabled":true,"ScheduleTime":"07:45"}""")!;
        Assert.Equal(new TimeOnly(7, 45), Assert.Single(legacy.GetScheduleTimes()));
        Assert.True(legacy.IsDue(new DateTime(2026, 10, 16, 7, 45, 0)));
    }

    [Fact]
    public void WindowsTaskContainsEachConfiguredTimeOnceAndKeepsNegativeAndLogonTriggers()
    {
        var settings = new AppSettings { ScheduleTime = "16:30, 09:00, 13:00, 09:00" };
        var document = WindowsScheduler.BuildTaskXml(settings, @"C:\MP Mail\MpMail.exe", "S-1-test");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var daily = document.Descendants(ns + "CalendarTrigger").Where(trigger => trigger.Element(ns + "Repetition") == null);
        Assert.Equal(new[] { "2020-01-01T09:00:00", "2020-01-01T13:00:00", "2020-01-01T16:30:00" },
            daily.Select(trigger => trigger.Element(ns + "StartBoundary")!.Value));
        Assert.Single(document.Descendants(ns + "Repetition"));
        Assert.Single(document.Descendants(ns + "LogonTrigger"));
    }

    [Fact]
    public async Task MultipleDailySlotsSendNewDocumentsOncePerSlotAndKeepTemplateDates()
    {
        using var workspace = new TestWorkspace();
        var customer = workspace.Customer with { Subject = "{data} / {miesiac}" };
        workspace.Repository.SaveCustomer(customer);
        workspace.Repository.SaveSettings(workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "09:00, 13:00" });
        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-001.pdf"), workspace.Customer.Name);
        await service.RunScheduledAsync(new DateTime(2026, 10, 16, 8, 59, 0), CancellationToken.None);
        Assert.Equal(0, sender.Count);
        await service.RunScheduledAsync(new DateTime(2026, 10, 16, 9, 0, 0), CancellationToken.None);
        Assert.Equal(1, sender.Count);
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-002.pdf"), workspace.Customer.Name);
        await service.RunScheduledAsync(new DateTime(2026, 10, 16, 12, 59, 0), CancellationToken.None);
        Assert.Equal(1, sender.Count);
        await service.RunScheduledAsync(new DateTime(2026, 10, 16, 13, 0, 0), CancellationToken.None);
        await service.RunScheduledAsync(new DateTime(2026, 10, 16, 13, 1, 0), CancellationToken.None);
        Assert.Equal(2, sender.Count);
        Assert.All(sender.Messages, mail => Assert.Equal("2026-10-16 / 2026-10", mail.Subject));
        Assert.True(File.Exists(Path.Combine(workspace.Source, "archiwum", "SCHED-2026-10-16-1300", "orzeczenie-profilaktyczne-p1-002.pdf")));
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-003.pdf"), workspace.Customer.Name);
        await service.RunScheduledAsync(new DateTime(2026, 10, 17, 9, 0, 0), CancellationToken.None);
        Assert.Equal(3, sender.Count);
    }

    [Fact]
    public async Task MissedSlotsAreNotCaughtUpAndUncertainDeliveryStillBlocksTheNextSlot()
    {
        using var workspace = new TestWorkspace();
        var customer = workspace.Customer;
        workspace.Repository.SaveCustomer(customer);
        workspace.Repository.SaveSettings(workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "09:00, 13:00, 16:00" });
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-001.pdf"), workspace.Customer.Name);
        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);
        await service.RunScheduledAsync(new DateTime(2026, 10, 16, 15, 0, 0), CancellationToken.None);
        Assert.Empty(sender.Messages);
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-002.pdf"), workspace.Customer.Name);
        workspace.Repository.Finish(customer, "SCHED-2026-10-16-1600", DeliveryStatus.Uncertain, "unknown", "message-test");
        await service.RunScheduledAsync(new DateTime(2026, 10, 16, 16, 0, 0), CancellationToken.None);
        Assert.Equal(0, sender.Count);
    }

    [Theory]
    [InlineData("bad-email", "1")]
    [InlineData("valid@example.test", "maybe")]
    public void CsvRowErrorsIncludeCompanyNameAndRow(string email, string active)
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "invalid.csv");
        File.WriteAllText(path, CustomerCsv.Header + "\n" +
            $";Firma z błędem;{email};Temat;Treść;{workspace.Source};secret;{active}\n");
        var exception = Assert.Throws<UserException>(() => CustomerCsv.Import(path, [], workspace.Secrets));
        Assert.Contains("Firma z błędem", ErrorMessages.Describe(exception));
        Assert.Contains("wiersz 2", exception.Message);
        Assert.DoesNotContain("secret", exception.Message);
    }

    [Fact]
    public void BulkSaveErrorNamesTheCompanyAndRollsBackEarlierRows()
    {
        using var workspace = new TestWorkspace();
        var first = workspace.Customer with { Name = "Poprawna firma" };
        var invalid = workspace.Customer with { Name = "Błędna firma", Email = "invalid" };
        var exception = Assert.Throws<UserException>(() => workspace.Repository.SaveCustomers([first, invalid]));
        Assert.Contains("Błędna firma", ErrorMessages.Describe(exception));
        Assert.Empty(workspace.Repository.Customers());
    }

    [Theory]
    [InlineData(DeliveryStatus.Pending)]
    [InlineData(DeliveryStatus.Uncertain)]
    public async Task UnresolvedEarlierDeliveryBlocksLaterSlotsForTheSameDay(DeliveryStatus status)
    {
        using var workspace = new TestWorkspace();
        var customer = workspace.Customer;
        workspace.Repository.SaveCustomer(customer);
        workspace.Repository.SaveSettings(workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "09:00, 13:00" });
        workspace.Repository.Finish(customer, "2026-10-16", status, "unresolved", "earlier-message");
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-001.pdf"), workspace.Customer.Name);
        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);
        await service.RunScheduledAsync(new DateTime(2026, 10, 16, 13, 0, 0), CancellationToken.None);
        Assert.Empty(sender.Messages);
        await service.RunScheduledAsync(new DateTime(2026, 10, 17, 9, 0, 0), CancellationToken.None);
        Assert.Single(sender.Messages);
    }

    [Fact]
    public void DatabaseSaveFailureIncludesCompanyAndKeepsImportAtomic()
    {
        using var workspace = new TestWorkspace();
        var first = workspace.Customer with { Name = "Firma poprawna" };
        var rejected = workspace.Customer with { Name = "Firma odrzucona" };
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(workspace.Root, "data.db")}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TRIGGER RejectCompany BEFORE INSERT ON Customers WHEN NEW.Id='{rejected.Id}' BEGIN SELECT RAISE(ABORT, 'test failure'); END;";
            command.ExecuteNonQuery();
        }
        var exception = Assert.Throws<UserException>(() => workspace.Repository.SaveCustomers([first, rejected]));
        Assert.Contains("Firma odrzucona", ErrorMessages.Describe(exception));
        Assert.IsType<Microsoft.Data.Sqlite.SqliteException>(exception.InnerException);
        Assert.Empty(workspace.Repository.Customers());
    }

    [Fact]
    public void ConfigurableFileRulesRespectOrderDisabledStateAndWildcards()
    {
        using var workspace = new TestWorkspace();
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "SPECIAL-001.pdf"), workspace.Customer.Name);
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "OTHER-001.pdf"), workspace.Customer.Name);
        File.WriteAllText(Path.Combine(workspace.Source, "NOTE-001.txt"), "note");
        var rules = new FileDispatchRuleSet
        {
            new() { Pattern = "SPECIAL-*.pdf", Enabled = false, Mode = FileDispatchMode.Interval, IntervalMinutes = 5 },
            new() { Pattern = "*.pdf", Enabled = true, Mode = FileDispatchMode.Scheduled },
            new() { Pattern = "SPECIAL-*.pdf", Enabled = true, Mode = FileDispatchMode.Interval, IntervalMinutes = 5 },
            new() { Pattern = "NOTE-???.txt", Enabled = true, Mode = FileDispatchMode.Interval, IntervalMinutes = 30 }
        };
        var archive = new ArchiveService(workspace.Secrets);

        var scheduled = archive.Discover(workspace.Source, 10 * 1048576L, rules,
            rule => rule.Mode == FileDispatchMode.Scheduled, "scheduled");
        var interval30 = archive.Discover(workspace.Source, 10 * 1048576L, rules,
            rule => rule.Mode == FileDispatchMode.Interval && rule.IntervalMinutes == 30, "30 min");

        Assert.Equal(new[] { "OTHER-001.pdf", "SPECIAL-001.pdf" },
            scheduled.Select(file => file.RelativePath).Order().ToArray());
        Assert.Equal("NOTE-001.txt", Assert.Single(interval30).RelativePath);
        Assert.False(archive.HasMatchingFiles(workspace.Source, rules,
            rule => rule.Mode == FileDispatchMode.Interval && rule.IntervalMinutes == 5));
    }

    [Fact]
    public async Task DifferentRuleIntervalsCreateSeparateBatches()
    {
        using var workspace = new TestWorkspace();
        var rules = new FileDispatchRuleSet
        {
            new() { Pattern = "FAST-*.pdf", Enabled = true, Mode = FileDispatchMode.Interval, IntervalMinutes = 5 },
            new() { Pattern = "SLOW-*.pdf", Enabled = true, Mode = FileDispatchMode.Interval, IntervalMinutes = 15 }
        };
        workspace.Repository.SaveCustomer(workspace.Customer);
        workspace.Repository.SaveSettings(workspace.Settings with
        {
            ScheduleEnabled = true,
            FileRules = rules
        });
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "FAST-001.pdf"), workspace.Customer.Name);
        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "SLOW-001.pdf"), workspace.Customer.Name);
        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);

        await service.RunAutomationsAsync(new DateTime(2026, 10, 22, 9, 7, 0), CancellationToken.None);

        Assert.Equal(2, sender.Count);
        Assert.True(File.Exists(Path.Combine(workspace.Source, "archiwum", "INT-5-2026-10-22-0905", "FAST-001.pdf")));
        Assert.True(File.Exists(Path.Combine(workspace.Source, "archiwum", "INT-15-2026-10-22-0900", "SLOW-001.pdf")));

        TestPdf.WriteMedicalCertificate(Path.Combine(workspace.Source, "FAST-002.pdf"), workspace.Customer.Name);
        await service.RunAutomationsAsync(new DateTime(2026, 10, 22, 9, 9, 0), CancellationToken.None);
        Assert.Equal(2, sender.Count);
        await service.RunAutomationsAsync(new DateTime(2026, 10, 22, 9, 10, 0), CancellationToken.None);
        Assert.Equal(3, sender.Count);
    }

    [Fact]
    public void WindowsTaskUsesDistinctIntervalsFromEnabledRules()
    {
        var settings = new AppSettings
        {
            ScheduleEnabled = true,
            ScheduleTime = "09:00, 13:00",
            FileRules =
            [
                new() { Pattern = "A*.pdf", Enabled = true, Mode = FileDispatchMode.Scheduled },
                new() { Pattern = "B*.pdf", Enabled = true, Mode = FileDispatchMode.Interval, IntervalMinutes = 5 },
                new() { Pattern = "C*.pdf", Enabled = true, Mode = FileDispatchMode.Interval, IntervalMinutes = 15 },
                new() { Pattern = "D*.pdf", Enabled = true, Mode = FileDispatchMode.Interval, IntervalMinutes = 15 },
                new() { Pattern = "E*.pdf", Enabled = false, Mode = FileDispatchMode.Interval, IntervalMinutes = 30 }
            ]
        };

        var document = WindowsScheduler.BuildTaskXml(settings, @"C:\MP Mail\MpMail.exe", "S-1-test");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal(new[] { "PT5M", "PT15M" },
            document.Descendants(ns + "Interval").Select(element => element.Value).ToArray());
        Assert.Equal(2, document.Descendants(ns + "CalendarTrigger")
            .Count(trigger => trigger.Element(ns + "Repetition") == null));
    }

    [Fact]
    public void LegacySettingsReceiveCurrentDefaultFileRules()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            """{"ScheduleEnabled":true,"ScheduleTime":"09:00","NegativeIntervalMinutes":15}""")!;

        Assert.Equal(2, legacy.FileRules.Count);
        Assert.Equal("orzeczenie-profilaktyczne-p1*.pdf", legacy.FileRules[0].Pattern);
        Assert.Equal(FileDispatchMode.Scheduled, legacy.FileRules[0].Mode);
        Assert.Equal("NEG.orzeczenie-profilaktyczne-p1*.pdf", legacy.FileRules[1].Pattern);
        Assert.Equal(FileDispatchMode.Interval, legacy.FileRules[1].Mode);
        Assert.Equal(15, legacy.FileRules[1].IntervalMinutes);
    }

    private sealed class CaptureProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];
        public void Report(string value) => Messages.Add(value);
    }

    private sealed class RecordingSender : IMailSender
    {
        public int Count { get; private set; }
        public int ErrorNotificationCount { get; private set; }
        public List<PreparedMail> Messages { get; } = [];
        public Task<SendOutcome> SendAsync(AppSettings settings, PreparedMail mail, string messageId, CancellationToken cancellationToken)
        {
            Count++;
            Messages.Add(mail);
            return Task.FromResult(new SendOutcome(DeliveryStatus.Success, "accepted"));
        }

        public Task NotifyErrorAsync(AppSettings settings, Customer customer, string period, DeliveryStatus status,
            string detail, CancellationToken cancellationToken)
        {
            ErrorNotificationCount++;
            return Task.CompletedTask;
        }
    }
}
