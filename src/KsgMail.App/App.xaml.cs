using System.IO;
using System.Windows;
using KsgMail.Core;

namespace KsgMail.App;

public partial class App : Application
{
    private FileStream? instanceLock;
    private ScheduledRunSignal? scheduleSignal;

    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        var scheduled = args.Args.Contains("--scheduled");
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MpMail");
        void Trace(string message) => ScheduleDiagnostics.Write(dataDirectory, message);
        try
        {
            Directory.CreateDirectory(dataDirectory);
            Trace(scheduled ? "Uruchomiono MP Mail z Harmonogramu Windows." : "Uruchomiono okno MP Mail.");
            try { instanceLock = new FileStream(Path.Combine(dataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                if (scheduled)
                {
                    var notified = await ScheduledRunSignal.NotifyAsync(ScheduledRunSignal.Name, TimeSpan.FromSeconds(5));
                    Trace(notified ? "Przekazano żądanie automatu do uruchomionego MP Mail." : "Nie udało się przekazać żądania automatu do uruchomionego MP Mail.");
                    Shutdown(notified ? 0 : 1);
                    return;
                }
                if (!scheduled) MessageBox.Show("Wysyłka masowa z medycyny pracy jest już uruchomiona dla tego konta Windows.", "MP Mail");
                Shutdown(0);
                return;
            }
            var tempRoot = Path.Combine(dataDirectory, "Temp");
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
            var repository = new Repository(Path.Combine(dataDirectory, "mp-mail.db"));
            repository.RecoverInterrupted();
            var secrets = new WindowsSecretProtector();
            var sender = new SmtpSender(secrets);
            var batches = new BatchService(repository, new ArchiveService(secrets), sender, tempRoot);
            if (scheduled)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try
                {
                    await batches.RunAutomationsAsync(DateTime.Now, CancellationToken.None, diagnostics: Trace);
                    Shutdown(0);
                }
                catch (Exception exception)
                {
                    Trace($"Błąd automatu: {ErrorMessages.Describe(exception)}");
                    repository.Log(new Customer { Name = "Harmonogram" }, DateTime.Now.ToString("yyyy-MM-dd"),
                        DeliveryStatus.Failed, ErrorMessages.Describe(exception));
                    Shutdown(1);
                }
                return;
            }
            var window = new MainWindow(repository, secrets, sender, batches, Trace);
            MainWindow = window;
            scheduleSignal = new ScheduledRunSignal(ScheduledRunSignal.Name, () =>
            {
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(new Action(window.RequestScheduleCheck));
            });
            if (repository.Settings().ScheduleEnabled)
            {
                try
                {
                    await new WindowsScheduler().ApplyAsync(repository.Settings(), Path.Combine(AppContext.BaseDirectory, "MpMail.exe"));
                    Trace("Odświeżono zadanie Windows: godziny wysyłki i wybudzanie z uśpienia.");
                }
                catch (Exception exception)
                {
                    Trace($"Nie udało się odświeżyć zadania Windows: {ErrorMessages.Describe(exception)}");
                }
            }
            window.Show();
        }
        catch (Exception exception)
        {
            Trace($"Błąd uruchomienia: {ErrorMessages.Describe(exception)}");
            if (!scheduled) MessageBox.Show(ErrorMessages.Describe(exception), "MP Mail", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs args)
    {
        scheduleSignal?.Dispose();
        instanceLock?.Dispose();
        base.OnExit(args);
    }
}
