using System.IO;
using System.Windows;
using KsgMail.Core;

namespace KsgMail.App;

public partial class App : Application
{
    private FileStream? instanceLock;

    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        var scheduled = args.Args.Contains("--scheduled");
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MpMail");
        try
        {
            Directory.CreateDirectory(dataDirectory);
            try { instanceLock = new FileStream(Path.Combine(dataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
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
                    await batches.RunAutomationsAsync(DateTime.Now, CancellationToken.None);
                    Shutdown(0);
                }
                catch (Exception exception)
                {
                    repository.Log(new Customer { Name = "Harmonogram" }, DateTime.Now.ToString("yyyy-MM-dd"),
                        DeliveryStatus.Failed, ErrorMessages.Describe(exception));
                    Shutdown(1);
                }
                return;
            }
            MainWindow = new MainWindow(repository, secrets, sender, batches);
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            if (!scheduled) MessageBox.Show(ErrorMessages.Describe(exception), "MP Mail", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs args)
    {
        instanceLock?.Dispose();
        base.OnExit(args);
    }
}
