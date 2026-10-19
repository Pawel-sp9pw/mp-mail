using System.IO;
using KsgMail.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KsgMail.Tests;

public sealed class StrictScheduleTests
{
    [Theory]
    [InlineData(11, 59, 59)]
    [InlineData(12, 1, 0)]
    [InlineData(12, 15, 0)]
    [InlineData(13, 1, 0)]
    [InlineData(13, 30, 0)]
    [InlineData(17, 59, 59)]
    [InlineData(18, 1, 0)]
    [InlineData(23, 59, 59)]
    public async Task StartupOrPeriodicCheckOutsideConfiguredMinuteCannotSendStandardDocuments(int hour, int minute, int second)
    {
        using var workspace = new TestWorkspace();
        workspace.Repository.SaveSettings(workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "12:00, 13:00, 18:00" });
        workspace.Repository.SaveCustomer(workspace.Customer);
        var document = Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-test.pdf");
        File.WriteAllText(document, "standard");
        var sender = new RecordingSender();
        var service = Service(workspace, sender);

        await service.RunAutomationsAsync(new DateTime(2026, 10, 19, hour, minute, second), CancellationToken.None);

        Assert.Empty(sender.Messages);
        Assert.True(File.Exists(document));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30000)]
    [InlineData(59999)]
    public async Task BatchMayStartAnywhereWithinTheConfiguredMinute(int milliseconds)
    {
        using var workspace = new TestWorkspace();
        workspace.Repository.SaveSettings(workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "13:00, 18:00" });
        workspace.Repository.SaveCustomer(workspace.Customer);
        File.WriteAllText(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-test.pdf"), "standard");
        var sender = new RecordingSender();

        await Service(workspace, sender).RunScheduledAsync(new DateTime(2026, 10, 19, 13, 0, 0).AddMilliseconds(milliseconds), CancellationToken.None);

        Assert.Single(sender.Messages);
    }

    [Fact]
    public async Task OneBatchPreparesAllCompaniesAndDoesNotAcceptLateFilesOrCompaniesEvenAfterRestart()
    {
        using var workspace = new TestWorkspace();
        workspace.Repository.SaveSettings(workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "13:00, 18:00" });
        var first = Company(workspace, "Pierwsza");
        var second = Company(workspace, "Druga");
        var late = Company(workspace, "Spóźniona");
        workspace.Repository.SaveCustomers([first, second, late]);
        Document(first, "first");
        Document(first, "second");
        Document(second, "first");
        var sender = new RecordingSender();
        sender.BeforeSend = _ => Assert.Equal(2, Directory.GetFiles(workspace.Temp, "*.zip", SearchOption.AllDirectories).Length);

        await Service(workspace, sender).RunScheduledAsync(new DateTime(2026, 10, 19, 13, 0, 5), CancellationToken.None);

        Assert.Equal(2, sender.Messages.Count);
        Assert.Equal(2, sender.Messages.Single(mail => mail.Customer.Id == first.Id).Files.Count);
        Assert.All(sender.Messages, mail => Assert.All(mail.Files, file => Assert.StartsWith(mail.Customer.SourceDirectory, file.FullPath)));
        sender.BeforeSend = null;
        Document(first, "late");
        Document(late, "late");
        var added = Company(workspace, "Dodana po rozpoczęciu");
        workspace.Repository.SaveCustomer(added);
        Document(added, "late");
        var reopened = new Repository(Path.Combine(workspace.Root, "data.db"));
        reopened.RecoverInterrupted();
        var restarted = new BatchService(reopened, new ArchiveService(workspace.Secrets), sender, workspace.Temp);

        await restarted.RunScheduledAsync(new DateTime(2026, 10, 19, 13, 0, 40), CancellationToken.None);
        await restarted.RunAutomationsAsync(new DateTime(2026, 10, 19, 13, 15, 0), CancellationToken.None);
        Assert.Equal(2, sender.Messages.Count);
        await restarted.RunScheduledAsync(new DateTime(2026, 10, 19, 18, 0, 0), CancellationToken.None);
        Assert.Equal(5, sender.Messages.Count);
    }

    [Fact]
    public async Task EmptyBatchIsAlsoClosedAndLaterFilesWaitForNextConfiguredTime()
    {
        using var workspace = new TestWorkspace();
        workspace.Repository.SaveSettings(workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "13:00, 18:00" });
        var company = workspace.Customer;
        workspace.Repository.SaveCustomer(company);
        var sender = new RecordingSender();
        var service = Service(workspace, sender);
        await service.RunScheduledAsync(new DateTime(2026, 10, 19, 13, 0, 0), CancellationToken.None);
        Document(company, "late");

        await service.RunScheduledAsync(new DateTime(2026, 10, 19, 13, 0, 30), CancellationToken.None);
        await service.RunScheduledAsync(new DateTime(2026, 10, 19, 13, 30, 0), CancellationToken.None);
        Assert.Empty(sender.Messages);
        await service.RunScheduledAsync(new DateTime(2026, 10, 19, 18, 0, 0), CancellationToken.None);
        Assert.Single(sender.Messages);
    }

    [Fact]
    public async Task FailedDeliveryDoesNotStartAnotherIndividualAttemptForTheSameBatch()
    {
        using var workspace = new TestWorkspace();
        workspace.Repository.SaveSettings(workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "13:00, 18:00" });
        var company = workspace.Customer;
        workspace.Repository.SaveCustomer(company);
        Document(company, "document");
        var sender = new RecordingSender { Outcome = DeliveryStatus.Failed };
        var service = Service(workspace, sender);
        await service.RunScheduledAsync(new DateTime(2026, 10, 19, 13, 0, 0), CancellationToken.None);
        await service.RunScheduledAsync(new DateTime(2026, 10, 19, 13, 0, 30), CancellationToken.None);
        await service.RunScheduledAsync(new DateTime(2026, 10, 19, 13, 15, 0), CancellationToken.None);
        Assert.Single(sender.Messages);
        sender.Outcome = DeliveryStatus.Success;
        await service.RunScheduledAsync(new DateTime(2026, 10, 19, 18, 0, 0), CancellationToken.None);
        Assert.Equal(2, sender.Messages.Count);
    }

    [Fact]
    public async Task OnlyNegativeDocumentsCanSendBetweenStandardBatchTimes()
    {
        using var workspace = new TestWorkspace();
        workspace.Repository.SaveSettings(workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "13:00, 18:00" });
        var company = workspace.Customer;
        workspace.Repository.SaveCustomer(company);
        Document(company, "standard");
        var negative = Path.Combine(company.SourceDirectory, "NEG.orzeczenie-profilaktyczne-p1-test.pdf");
        File.WriteAllText(negative, "negative");
        var sender = new RecordingSender();
        var service = Service(workspace, sender);

        await service.RunAutomationsAsync(new DateTime(2026, 10, 19, 13, 15, 0), CancellationToken.None);

        var mail = Assert.Single(sender.Messages);
        Assert.Equal("NEG-2026-10-19-1315", mail.Period);
        Assert.Equal(negative, Assert.Single(mail.Files).FullPath);
        Assert.Single(Directory.GetFiles(company.SourceDirectory, "*.pdf"));
    }

    [Fact]
    public async Task BatchClaimIsAtomicAcrossRepositoryInstances()
    {
        using var workspace = new TestWorkspace();
        var other = new Repository(Path.Combine(workspace.Root, "data.db"));
        var attempts = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            (index % 2 == 0 ? workspace.Repository : other).TryStartScheduledBatch("SCHED-2026-10-19-1300")));
        var results = await Task.WhenAll(attempts);
        Assert.Equal(1, results.Count(claimed => claimed));
    }

    [Fact]
    public void AdditiveUpgradePreservesExistingCompaniesSettingsPasswordsHistoryAndDispatchBlocks()
    {
        using var workspace = new TestWorkspace();
        var company = workspace.Customer;
        var settings = workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "13:00, 18:00" };
        workspace.Repository.SaveCustomer(company);
        workspace.Repository.SaveSettings(settings);
        workspace.Repository.Finish(company, "2026-10-19", DeliveryStatus.Success, "sent before upgrade", "legacy-message");
        var history = workspace.Repository.Logs();
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(workspace.Root, "data.db")}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE ScheduledBatches; PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }

        var upgraded = new Repository(Path.Combine(workspace.Root, "data.db"));

        Assert.Equal(company, Assert.Single(upgraded.Customers()));
        Assert.Equal(settings, upgraded.Settings());
        Assert.Equal(history, upgraded.Logs());
        Assert.True(upgraded.IsBlocked(company.Id, "2026-10-19"));
        Assert.Equal("zip-secret", workspace.Secrets.Unprotect(Assert.Single(upgraded.Customers()).ZipPasswordProtected));
        Assert.Equal("smtp-secret", workspace.Secrets.Unprotect(upgraded.Settings().PasswordProtected));
        Assert.True(upgraded.TryStartScheduledBatch("SCHED-2026-10-19-1800"));
        Assert.False(new Repository(Path.Combine(workspace.Root, "data.db")).TryStartScheduledBatch("SCHED-2026-10-19-1800"));
    }

    private static Customer Company(TestWorkspace workspace, string name)
    {
        var directory = Path.Combine(workspace.Root, name);
        Directory.CreateDirectory(directory);
        return workspace.Customer with { Name = name, SourceDirectory = directory };
    }

    private static void Document(Customer company, string suffix) =>
        File.WriteAllText(Path.Combine(company.SourceDirectory, $"orzeczenie-profilaktyczne-p1-{suffix}.pdf"), "document");

    private static BatchService Service(TestWorkspace workspace, IMailSender sender) =>
        new(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);

    private sealed class RecordingSender : IMailSender
    {
        public List<PreparedMail> Messages { get; } = [];
        public Action<PreparedMail>? BeforeSend { get; set; }
        public DeliveryStatus Outcome { get; set; } = DeliveryStatus.Success;

        public Task<SendOutcome> SendAsync(AppSettings settings, PreparedMail mail, string messageId, CancellationToken cancellationToken)
        {
            BeforeSend?.Invoke(mail);
            Messages.Add(mail);
            return Task.FromResult(new SendOutcome(Outcome, "test: no real email sent"));
        }
    }
}
