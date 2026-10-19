using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using System.Xml.Linq;
using KsgMail.App;
using KsgMail.Core;
using Xunit;

namespace KsgMail.Tests;

public sealed class ScheduleIncidentTests
{
    [Fact]
    public async Task NoonAndEveningScheduleSendsAtBothTimes()
    {
        using var workspace = new TestWorkspace();
        workspace.Repository.SaveSettings(workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "12:00, 18:00" });
        workspace.Repository.SaveCustomer(workspace.Customer);
        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);
        var document = Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-test.pdf");
        File.WriteAllText(document, "noon");

        await service.RunAutomationsAsync(new DateTime(2026, 10, 19, 11, 59, 0), CancellationToken.None);
        Assert.Equal(0, sender.Count);
        await service.RunAutomationsAsync(new DateTime(2026, 10, 19, 12, 0, 0), CancellationToken.None);
        Assert.Equal(1, sender.Count);
        File.WriteAllText(document, "evening");
        await service.RunAutomationsAsync(new DateTime(2026, 10, 19, 17, 59, 0), CancellationToken.None);
        Assert.Equal(1, sender.Count);
        await service.RunAutomationsAsync(new DateTime(2026, 10, 19, 18, 0, 0), CancellationToken.None);
        Assert.Equal(2, sender.Count);

        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var task = WindowsScheduler.BuildTaskXml(workspace.Repository.Settings(), @"C:\MP Mail\MpMail.exe", "S-1-test");
        var boundaries = task.Descendants(ns + "StartBoundary").Select(element => element.Value).ToList();
        Assert.Contains("2020-01-01T12:00:00", boundaries);
        Assert.Contains("2020-01-01T18:00:00", boundaries);
        Assert.Equal("true", task.Descendants(ns + "WakeToRun").Single().Value);
        Assert.Equal("InteractiveToken", task.Descendants(ns + "LogonType").Single().Value);
    }

    [Fact]
    public void AutomationSendsWithHiddenWindowAndClearsOnlyObsoletePreview()
    {
        RunOnSta(async () =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "12:00, 18:00" };
            workspace.Repository.SaveSettings(settings);
            var customer = workspace.Customer;
            workspace.Repository.SaveCustomer(customer);
            var document = Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-test.pdf");
            File.WriteAllText(document, "noon");
            var sender = new RecordingSender();
            var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);
            var trace = new List<string>();
            var window = new MainWindow(workspace.Repository, workspace.Secrets, new SmtpSender(workspace.Secrets), service, trace.Add);
            try
            {
                var fields = BindingFlags.NonPublic | BindingFlags.Instance;
                ((DispatcherTimer)typeof(MainWindow).GetField("timer", fields)!.GetValue(window)!).Stop();
                var previewField = typeof(MainWindow).GetField("prepared", fields)!;
                var preview = await service.PrepareAsync([customer], settings, "2026-10", false, null, CancellationToken.None);
                var previewDirectory = Path.GetDirectoryName(Assert.Single(preview.Items).ArchivePath)!;
                previewField.SetValue(window, preview);
                Assert.False(window.IsVisible);
                await window.CheckScheduleAsync(new DateTime(2026, 10, 19, 12, 0, 0));
                Assert.Equal(1, sender.Count);
                Assert.Null(previewField.GetValue(window));
                Assert.False(Directory.Exists(previewDirectory));
                Assert.Contains(trace, message => message.Contains("Wyczyszczono podgląd"));

                File.WriteAllText(document, "evening");
                var eveningPreview = await service.PrepareAsync([customer], settings, "2026-10", false, null, CancellationToken.None);
                previewField.SetValue(window, eveningPreview);
                await window.CheckScheduleAsync(new DateTime(2026, 10, 19, 17, 59, 0));
                Assert.Same(eveningPreview, previewField.GetValue(window));
                Assert.Equal(1, sender.Count);
                await window.CheckScheduleAsync(new DateTime(2026, 10, 19, 18, 0, 0));
                Assert.Equal(2, sender.Count);
                Assert.Null(previewField.GetValue(window));
                Assert.Equal(2, workspace.Repository.Logs().Count(log => log.Status == DeliveryStatus.Success));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task NoMatchingDocumentsAlsoLeavesHistoryEmpty()
    {
        using var workspace = new TestWorkspace();
        workspace.Repository.SaveSettings(workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "12:00, 18:00" });
        workspace.Repository.SaveCustomer(workspace.Customer);
        File.WriteAllText(Path.Combine(workspace.Source, "unrelated.pdf"), "document");
        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);
        var diagnostics = new List<string>();

        await service.RunAutomationsAsync(new DateTime(2026, 10, 19, 12, 0, 0), CancellationToken.None,
            diagnostics: diagnostics.Add);

        Assert.Equal(0, sender.Count);
        Assert.Empty(workspace.Repository.Logs());
        Assert.Contains(diagnostics, message => message.Contains("brak pasujących plików lub niedostępny katalog 1"));
    }

    [Fact]
    public void WindowsTaskSignalQueuesDuringWorkAndDoesNotDuplicateDelivery()
    {
        RunOnSta(async () =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "00:00" };
            workspace.Repository.SaveSettings(settings);
            workspace.Repository.SaveCustomer(workspace.Customer);
            File.WriteAllText(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-test.pdf"), "document");
            var sender = new RecordingSender();
            var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);
            var window = new MainWindow(workspace.Repository, workspace.Secrets, new SmtpSender(workspace.Secrets), service);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var fields = BindingFlags.NonPublic | BindingFlags.Instance;
                ((DispatcherTimer)typeof(MainWindow).GetField("timer", fields)!.GetValue(window)!).Stop();
                var name = @"Local\MpMailTest-" + Guid.NewGuid().ToString("N");
                using var signal = new ScheduledRunSignal(name, () => window.Dispatcher.BeginInvoke(new Action(window.RequestScheduleCheck)));
                var manual = (Task)typeof(MainWindow).GetMethod("RunAsync", fields)!.Invoke(window,
                    [new Func<CancellationToken, Task>(_ => release.Task)])!;
                Assert.True(await ScheduledRunSignal.NotifyAsync(name, TimeSpan.FromSeconds(1)));
                await WaitUntilAsync(() => (bool)typeof(MainWindow).GetField("scheduleCheckRequested", fields)!.GetValue(window)!);
                Assert.Equal(0, sender.Count);
                release.SetResult();
                await manual;
                await WaitUntilAsync(() => sender.Count == 1 && !(bool)typeof(MainWindow).GetField("busy", fields)!.GetValue(window)!);
                Assert.True(await ScheduledRunSignal.NotifyAsync(name, TimeSpan.FromSeconds(1)));
                await Task.Delay(150);
                Assert.Equal(1, sender.Count);
            }
            finally
            {
                release.TrySetResult();
                window.Close();
            }
        });
    }

    [Fact]
    public async Task MissingReceiverIsReportedInsteadOfClaimingSuccess()
    {
        Assert.False(await ScheduledRunSignal.NotifyAsync(@"Local\MpMailMissing-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task ScheduledStandardDeliveryPrecedesNegativeBatch()
    {
        using var workspace = new TestWorkspace();
        workspace.Repository.SaveSettings(workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "12:00, 18:00" });
        workspace.Repository.SaveCustomer(workspace.Customer);
        File.WriteAllText(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-test.pdf"), "standard");
        File.WriteAllText(Path.Combine(workspace.Source, "NEG.orzeczenie-profilaktyczne-p1-test.pdf"), "negative");
        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);
        await service.RunAutomationsAsync(new DateTime(2026, 10, 19, 12, 0, 0), CancellationToken.None);
        Assert.Equal(new[] { "2026-10-19", "NEG-2026-10-19-1200" }, sender.Periods);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticLoggingCannotBlockDelivery(bool blockedDirectory)
    {
        using var workspace = new TestWorkspace();
        workspace.Repository.SaveSettings(workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "12:00, 18:00" });
        workspace.Repository.SaveCustomer(workspace.Customer);
        File.WriteAllText(Path.Combine(workspace.Source, "orzeczenie-profilaktyczne-p1-test.pdf"), "document");
        var dataDirectory = Path.Combine(workspace.Root, "diagnostics");
        if (blockedDirectory) File.WriteAllText(dataDirectory, "blocked");
        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);
        await service.RunAutomationsAsync(new DateTime(2026, 10, 19, 12, 0, 0), CancellationToken.None,
            diagnostics: message => ScheduleDiagnostics.Write(dataDirectory, message));
        Assert.Equal(1, sender.Count);
        if (!blockedDirectory)
        {
            var log = Assert.Single(Directory.GetFiles(Path.Combine(dataDirectory, "Logs"), "schedule-*.log"));
            var text = File.ReadAllText(log);
            Assert.Contains("12:00, 18:00", text);
            Assert.Contains("Zakończono sprawdzanie automatu", text);
            Assert.DoesNotContain("smtp-secret", text);
            Assert.DoesNotContain("zip-secret", text);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(ready(), "Scheduled work did not finish.");
    }

    private static void RunOnSta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); }
                catch (Exception exception) { failure = exception; }
                finally { dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Schedule diagnostic did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class RecordingSender : IMailSender
    {
        public int Count { get; private set; }
        public List<string> Periods { get; } = [];

        public Task<SendOutcome> SendAsync(AppSettings settings, PreparedMail mail, string messageId, CancellationToken cancellationToken)
        {
            Count++;
            Periods.Add(mail.Period);
            return Task.FromResult(new SendOutcome(DeliveryStatus.Success, "diagnostic: no email sent"));
        }
    }
}
