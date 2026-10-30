using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KsgMail.Core;
using Microsoft.Win32;
using Validation = KsgMail.Core.Validation;

namespace KsgMail.App;

public partial class MainWindow : Window
{
    private readonly Repository repository;
    private readonly ISecretProtector secrets;
    private readonly SmtpSender sender;
    private readonly BatchService batches;
    private readonly WindowsScheduler scheduler = new();
    private readonly GitHubUpdateService updater = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private Customer editing = new();
    private AppSettings settings = new();
    private PreparedBatch? prepared;
    private bool preparedResend;
    private CancellationTokenSource? operation;
    private bool busy;
    private DateTime nextScheduleCheck = DateTime.MinValue;
    private UpdateInfo? availableUpdate;
    private bool initialUpdateCheckDone;
    private List<Customer> allCustomers = [];
    private readonly Action<string> scheduleTrace;
    private bool scheduleCheckRequested;
    private readonly ObservableCollection<FileDispatchRule> fileRules = [];
    private List<DeliveryLog> allLogs = [];
    private bool loadingHistoryFilters;

    public MainWindow(Repository repository, ISecretProtector secrets, SmtpSender sender, BatchService batches,
        Action<string>? scheduleTrace = null)
    {
        this.repository = repository;
        this.secrets = secrets;
        this.sender = sender;
        this.batches = batches;
        this.scheduleTrace = scheduleTrace ?? (_ => { });
        InitializeComponent();
        Period.Text = DateTime.Now.ToString("yyyy-MM");
        LoadSettings();
        LoadCustomers();
        ShowCustomer(new Customer());
        LoadLogs();
        CurrentVersionText.Text = GitHubUpdateService.CurrentVersion.ToString(3);
        LatestVersionText.Text = "—";
        timer.Tick += ScheduleTick;
        timer.Start();
    }

    private void LoadCustomers()
    {
        allCustomers = repository.Customers().ToList();
        ApplyCustomerFilter();
    }

    private void ApplyCustomerFilter()
    {
        if (CustomersGrid == null) return;
        var search = CustomerSearch?.Text?.Trim() ?? "";
        var state = CustomerStateFilter?.SelectedIndex ?? 0;
        var filtered = allCustomers.Where(customer =>
            (search.Length == 0 ||
             customer.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
             customer.Email.Contains(search, StringComparison.CurrentCultureIgnoreCase)) &&
            (state == 0 || state == 1 && customer.Active || state == 2 && !customer.Active))
            .ToList();

        CustomersGrid.ItemsSource = filtered;
        if (CustomerCountText != null)
            CustomerCountText.Text = $"Widoczne: {filtered.Count} / {allCustomers.Count}";
    }

    private void CustomerFilterChanged(object senderObject, RoutedEventArgs args) => ApplyCustomerFilter();

    private List<Customer> SelectedCustomers() => CustomersGrid.SelectedItems.Cast<Customer>().ToList();

    private void SelectAllVisibleCustomers(object senderObject, RoutedEventArgs args)
    {
        CustomersGrid.SelectAll();
        StatusText.Text = $"Zaznaczono {CustomersGrid.SelectedItems.Count} widocznych firm.";
    }

    private void ClearCustomerSelection(object senderObject, RoutedEventArgs args)
    {
        CustomersGrid.UnselectAll();
        StatusText.Text = "Wyczyszczono zaznaczenie firm.";
    }

    private void ShowCustomer(Customer customer)
    {
        editing = customer;
        CustomerName.Text = customer.Name;
        CustomerEmail.Text = customer.Email;
        CustomerSubject.Text = customer.Subject;
        CustomerBody.Text = customer.Body;
        CustomerDirectory.Text = customer.SourceDirectory;
        CustomerPassword.Clear();
        ZipPasswordLabel.Text = customer.ZipPasswordProtected.Length == 0 ? "Hasło ZIP" : "Hasło ZIP (zapisane; wpisz, aby zmienić)";
        CustomerActive.IsChecked = customer.Active;
    }

    private void NewCustomer(object senderObject, RoutedEventArgs args)
    {
        CustomersGrid.SelectedItem = null;
        ShowCustomer(new Customer());
    }

    private void CustomerSelected(object senderObject, SelectionChangedEventArgs args)
    {
        if (CustomersGrid.SelectedItems.Count == 1 && CustomersGrid.SelectedItem is Customer customer)
            ShowCustomer(customer);
    }

    private void BrowseDirectory(object senderObject, RoutedEventArgs args)
    {
        var dialog = new OpenFolderDialog { Title = "Katalog dokumentów firmy" };
        if (dialog.ShowDialog(this) == true) CustomerDirectory.Text = dialog.FolderName;
    }

    private void SaveCustomer(object senderObject, RoutedEventArgs args) => Guard(() =>
    {
        var customer = editing with
        {
            Name = CustomerName.Text.Trim(), Email = CustomerEmail.Text.Trim(),
            Subject = CustomerSubject.Text.Trim(), Body = CustomerBody.Text,
            SourceDirectory = CustomerDirectory.Text.Trim(), Active = CustomerActive.IsChecked == true,
            ZipPasswordProtected = CustomerPassword.Password.Length == 0 ? editing.ZipPasswordProtected : secrets.Protect(CustomerPassword.Password)
        };
        ValidateSourceDirectory(customer);
        repository.SaveCustomer(customer);
        InvalidatePreview();
        LoadCustomers();
        CustomersGrid.SelectedItem = allCustomers.FirstOrDefault(item => item.Id == customer.Id);
        ShowCustomer(customer);
        StatusText.Text = $"Zapisano firmę: {customer.Name}.";
    }, CustomerName.Text.Trim());

    private static void ValidateSourceDirectory(Customer customer)
    {
        Validation.Customer(customer);
        var dataRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MpMail"));
        var source = Path.GetFullPath(customer.SourceDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if ((dataRoot + Path.DirectorySeparatorChar).StartsWith(source, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(dataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UserException("Wybierz katalog dokumentów poza katalogiem danych MP Mail.");
    }

    private void DeleteCustomer(object senderObject, RoutedEventArgs args) => Guard(() =>
    {
        var selected = SelectedCustomers();
        if (selected.Count == 0) throw new UserException("Zaznacz co najmniej jedną firmę.");
        var message = selected.Count == 1
            ? $"Usunąć firmę {selected[0].Name}? Historia wysyłek pozostanie."
            : $"Usunąć {selected.Count} zaznaczonych firm? Historia wysyłek pozostanie.";
        if (MessageBox.Show(this, message, "Usuń firmy",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var customer in selected) repository.DeleteCustomer(customer.Id);
        InvalidatePreview();
        LoadCustomers();
        ShowCustomer(new Customer());
        StatusText.Text = $"Usunięto {selected.Count} firm.";
    });

    private void ActivateSelectedCustomers(object senderObject, RoutedEventArgs args) => Guard(() =>
    {
        var selected = SelectedCustomers();
        if (selected.Count == 0) throw new UserException("Zaznacz co najmniej jedną firmę.");

        int activated = 0, skipped = 0;
        foreach (var customer in selected)
        {
            var active = customer with { Active = true };
            try
            {
                Validation.Customer(active);
                repository.SaveCustomer(active);
                activated++;
            }
            catch (UserException)
            {
                skipped++;
            }
        }

        InvalidatePreview();
        LoadCustomers();
        StatusText.Text = skipped == 0
            ? $"Aktywowano {activated} firm."
            : $"Aktywowano {activated} firm. Pominięto {skipped} bez wymaganych danych (e-mail/hasło ZIP lub inny błąd konfiguracji).";
    });

    private void DeactivateSelectedCustomers(object senderObject, RoutedEventArgs args) => Guard(() =>
    {
        var selected = SelectedCustomers();
        if (selected.Count == 0) throw new UserException("Zaznacz co najmniej jedną firmę.");
        foreach (var customer in selected)
            repository.SaveCustomer(customer with { Active = false });

        InvalidatePreview();
        LoadCustomers();
        StatusText.Text = $"Dezaktywowano {selected.Count} firm.";
    });

    private void ExportCsv(object senderObject, RoutedEventArgs args) => Guard(() =>
    {
        var dialog = new SaveFileDialog
        {
            Title = "Eksport listy firm",
            Filter = "Plik CSV (*.csv)|*.csv",
            FileName = $"firmy-{DateTime.Now:yyyy-MM-dd}.csv",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        CustomerCsv.Export(dialog.FileName, repository.Customers());
        StatusText.Text = $"Wyeksportowano {repository.Customers().Count} firm. Hasła ZIP nie są eksportowane.";
    });

    private async void ImportCsv(object senderObject, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog { Title = "Import listy firm", Filter = "Plik CSV (*.csv)|*.csv" };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync(async cancellationToken =>
        {
            var progress = new Progress<string>(text => StatusText.Text = text);
            var result = await Task.Run(() =>
            {
                var imported = CustomerCsv.Import(dialog.FileName, repository.Customers(), secrets, progress);
                foreach (var customer in imported.Customers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { ValidateSourceDirectory(customer); }
                    catch (Exception exception)
                    {
                        throw new UserException(ErrorMessages.ForCustomer(customer.Name, exception), exception);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                repository.SaveCustomers(imported.Customers, progress);
                return imported;
            }, cancellationToken);
            InvalidatePreview();
            LoadCustomers();
            StatusText.Text = $"Import zakończony: nowych {result.Added}, zaktualizowanych {result.Updated}." +
                (result.Customers.Count == 0 ? "" : $" Zapisano firmy: {string.Join(", ", result.Customers.Select(customer => customer.Name))}.");
        });
    }

    private void LoadSettings()
    {
        settings = repository.Settings();
        SmtpHost.Text = settings.Host;
        SmtpPort.Text = settings.Port.ToString(CultureInfo.InvariantCulture);
        SmtpSecurity.SelectedIndex = settings.Security == TransportSecurity.StartTls ? 0 : 1;
        SmtpUsername.Text = settings.Username;
        SmtpPassword.Clear();
        SmtpPasswordLabel.Text = settings.PasswordProtected.Length == 0 ? "Hasło SMTP" : "Hasło SMTP (zapisane; wpisz, aby zmienić)";
        SenderEmail.Text = settings.SenderEmail;
        SenderName.Text = settings.SenderName;
        ErrorNotificationEmail.Text = settings.ErrorNotificationEmail;
        AttachmentLimit.Text = settings.MaxAttachmentMb.ToString(CultureInfo.InvariantCulture);
        SourceLimit.Text = settings.MaxSourceMb.ToString(CultureInfo.InvariantCulture);
        RetryCount.Text = settings.RetryCount.ToString(CultureInfo.InvariantCulture);
        ScheduleEnabled.IsChecked = settings.ScheduleEnabled;
        ScheduleTime.Text = settings.ScheduleTime;
        ArchiveRetentionDays.Text = settings.ArchiveRetentionDays.ToString(CultureInfo.InvariantCulture);
        fileRules.Clear();
        foreach (var rule in settings.FileRules ?? [])
            fileRules.Add(rule.Clone());
        FileRulesGrid.ItemsSource = fileRules;
    }

    private static int Number(TextBox field, string name)
    {
        if (!int.TryParse(field.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw new UserException($"{name}: podaj liczbę całkowitą.");
        return number;
    }

    private AppSettings ReadSettings()
    {
        var result = settings with
        {
            Host = SmtpHost.Text.Trim(), Port = Number(SmtpPort, "Port"),
            Security = SmtpSecurity.SelectedIndex == 0 ? TransportSecurity.StartTls : TransportSecurity.SslOnConnect,
            Username = SmtpUsername.Text.Trim(),
            PasswordProtected = SmtpPassword.Password.Length == 0 ? settings.PasswordProtected : secrets.Protect(SmtpPassword.Password),
            SenderEmail = SenderEmail.Text.Trim(), SenderName = SenderName.Text.Trim(),
            ErrorNotificationEmail = ErrorNotificationEmail.Text.Trim(),
            MaxAttachmentMb = Number(AttachmentLimit, "Limit ZIP"), MaxSourceMb = Number(SourceLimit, "Limit plików"),
            RetryCount = Number(RetryCount, "Liczba prób"), ScheduleEnabled = ScheduleEnabled.IsChecked == true,
            ScheduleTime = ScheduleTime.Text.Trim(),
            ArchiveRetentionDays = Number(ArchiveRetentionDays, "Retencja archiwum"),
            FileRules = new FileDispatchRuleSet(fileRules.Select(rule => rule.Clone()))
        };
        Validation.Settings(result);
        return result with
        {
            ScheduleTime = string.Join(", ", result.GetScheduleTimes().Select(time => time.ToString("HH:mm", CultureInfo.InvariantCulture)))
        };
    }

    private void AddFileRule(object senderObject, RoutedEventArgs args)
    {
        var rule = new FileDispatchRule
        {
            Pattern = "*.pdf",
            Enabled = true,
            Mode = FileDispatchMode.Scheduled,
            IntervalMinutes = 15
        };
        fileRules.Add(rule);
        FileRulesGrid.SelectedItem = rule;
        FileRulesGrid.ScrollIntoView(rule);
    }

    private void RemoveFileRule(object senderObject, RoutedEventArgs args)
    {
        if (FileRulesGrid.SelectedItem is not FileDispatchRule rule)
        {
            ShowError(new UserException("Wybierz regułę do usunięcia."));
            return;
        }
        fileRules.Remove(rule);
    }

    private void ToggleFileRule(object senderObject, RoutedEventArgs args)
    {
        if (FileRulesGrid.SelectedItem is not FileDispatchRule rule)
        {
            ShowError(new UserException("Wybierz regułę do włączenia lub wyłączenia."));
            return;
        }
        rule.Enabled = !rule.Enabled;
        FileRulesGrid.Items.Refresh();
        FileRulesGrid.SelectedItem = rule;
    }

    private void MoveFileRuleUp(object senderObject, RoutedEventArgs args)
    {
        if (FileRulesGrid.SelectedItem is not FileDispatchRule rule) return;
        var index = fileRules.IndexOf(rule);
        if (index <= 0) return;
        fileRules.Move(index, index - 1);
        FileRulesGrid.SelectedItem = rule;
    }

    private void MoveFileRuleDown(object senderObject, RoutedEventArgs args)
    {
        if (FileRulesGrid.SelectedItem is not FileDispatchRule rule) return;
        var index = fileRules.IndexOf(rule);
        if (index < 0 || index >= fileRules.Count - 1) return;
        fileRules.Move(index, index + 1);
        FileRulesGrid.SelectedItem = rule;
    }

    private async void SaveSettings(object senderObject, RoutedEventArgs args) => await RunAsync(async cancellationToken =>
    {
        var result = ReadSettings();
        await scheduler.ApplyAsync(result, Path.Combine(AppContext.BaseDirectory, "MpMail.exe"));
        repository.SaveSettings(result);
        InvalidatePreview();
        LoadSettings();
        StatusText.Text = "Zapisano SMTP, limity i harmonogram.";
    });

    private async void TestConnection(object senderObject, RoutedEventArgs args) => await RunAsync(async cancellationToken =>
    {
        await sender.TestAsync(ReadSettings(), cancellationToken);
        StatusText.Text = "Połączenie TLS i logowanie SMTP poprawne. Nie wysłano wiadomości.";
    });

    private async void PreviewAll(object senderObject, RoutedEventArgs args) =>
        await PrepareAsync(repository.Customers().Where(customer => customer.Active).ToList());

    private async void PreviewSelected(object senderObject, RoutedEventArgs args)
    {
        if (CustomersGrid.SelectedItem is not Customer customer)
        {
            MessageBox.Show(this, "Najpierw wybierz firmę w zakładce Firmy.", "MP Mail");
            return;
        }
        await PrepareAsync([customer]);
    }

    private async Task PrepareAsync(IReadOnlyList<Customer> customers) => await RunAsync(async cancellationToken =>
    {
        if (!customers.Any(customer => customer.Active)) throw new UserException("Brak aktywnych firm.");
        InvalidatePreview();

        // Tryb ręczny zawsze wymusza świeże sprawdzenie plików i PDF-ów.
        // Historia wcześniejszych wysyłek nie może pominąć walidacji ani zablokować świadomej wysyłki ręcznej.
        preparedResend = true;
        prepared = await batches.PrepareAsync(customers, repository.Settings(), Period.Text.Trim(), true,
            new Progress<string>(text => StatusText.Text = text), cancellationToken);
        PreviewGrid.ItemsSource = prepared.Items;
        SendButton.IsEnabled = prepared.Items.Any(mail => mail.Ready);
        var previewErrors = prepared.Items.Count(mail => mail.Error != null && !mail.Skipped);
        var previewWarnings = prepared.Items.Count(mail => mail.Skipped);
        StatusText.Text = $"Podgląd ręczny po ponownej weryfikacji: {prepared.Items.Count(mail => mail.Ready)} do wysłania, " +
            $"{previewWarnings} ostrzeżeń, {previewErrors} błędów.";
    });

    private void PreviewSelectedMail(object senderObject, SelectionChangedEventArgs args)
    {
        if (PreviewGrid.SelectedItem is not PreparedMail mail) return;
        if (!string.IsNullOrWhiteSpace(mail.Error))
        {
            PreviewSubject.Text = mail.Skipped ? $"Ostrzeżenie — {mail.Name}" : $"Błąd — {mail.Name}";
            PreviewBody.Text = mail.Error;
        }
        else
        {
            PreviewSubject.Text = mail.Subject;
            PreviewBody.Text = mail.Body;
        }
        PreviewFiles.ItemsSource = mail.Files;
    }

    private async void SendPrepared(object senderObject, RoutedEventArgs args)
    {
        if (prepared == null) return;

        var preview = prepared;
        var customers = preview.Items
            .Select(mail => mail.Customer)
            .GroupBy(customer => customer.Id)
            .Select(group => group.First())
            .ToList();
        var period = Period.Text.Trim();
        var count = preview.Items.Count(mail => mail.Ready);

        if (MessageBox.Show(this,
                $"Ponownie zweryfikować aktualne pliki i wysłać {count} wiadomości?\n\n" +
                "Ręczna wysyłka ignoruje blokadę wcześniejszej wysyłki dla tego okresu. " +
                "Przed SMTP wszystkie PDF-y zostaną odczytane i sprawdzone ponownie.",
                "Potwierdź ręczną wysyłkę", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (prepared != preview)
        {
            StatusText.Text = "Podgląd został wyczyszczony przez automat. Przygotuj nowy podgląd przed ręczną wysyłką.";
            return;
        }

        await RunAsync(async cancellationToken =>
        {
            StatusText.Text = "Ponowna weryfikacja plików przed ręczną wysyłką…";

            // Nie używamy ZIP-a z wcześniejszego podglądu. Budujemy wszystko od nowa,
            // aby wymusić ponowny odczyt PDF i porównanie pracodawcy bezpośrednio przed SMTP.
            using var verified = await batches.PrepareAsync(
                customers,
                repository.Settings(),
                period,
                true,
                new Progress<string>(text => StatusText.Text = text),
                cancellationToken);

            var ready = verified.Items.Count(mail => mail.Ready);
            var errors = verified.Items.Count(mail => mail.Error != null && !mail.Skipped);
            var warnings = verified.Items.Count(mail => mail.Skipped);

            if (ready == 0)
            {
                // SendAsync zapisze błędy i ostrzeżenia do historii.
                // W trybie ręcznym powiadomienia e-mail o błędach są celowo wyłączone.
                await batches.SendAsync(verified, true,
                    new Progress<string>(text => StatusText.Text = text), cancellationToken, notifyErrors: false);
                InvalidatePreview();
                StatusText.Text = errors > 0
                    ? $"Ręczna wysyłka wstrzymana po ponownej weryfikacji: {errors} błędów, {warnings} ostrzeżeń, 0 poprawnych paczek."
                    : $"Brak paczek do wysłania: {warnings} ostrzeżeń (np. pusty katalog).";
                LoadLogs();
                return;
            }

            await batches.SendAsync(verified, true,
                new Progress<string>(text => StatusText.Text = text), cancellationToken, notifyErrors: false);

            InvalidatePreview();
            LoadLogs();
            StatusText.Text = errors == 0
                ? $"Zakończono ręczną wysyłkę: wysłano {ready}, ostrzeżeń {warnings}."
                : $"Zakończono ręczną wysyłkę: wysłano {ready}, ostrzeżeń {warnings}, błędów {errors}.";
        });
    }

    private void ClearPreview(object senderObject, RoutedEventArgs args) => Guard(InvalidatePreview);

    private void InvalidatePreview()
    {
        prepared?.Dispose();
        prepared = null;
        PreviewGrid.ItemsSource = null;
        PreviewSubject.Text = "";
        PreviewBody.Text = "";
        PreviewFiles.ItemsSource = null;
        SendButton.IsEnabled = false;
    }

    private void LoadLogs()
    {
        allLogs = repository.Logs().ToList();
        var selectedCompany = HistoryCompanyFilter?.SelectedItem as string ?? "Wszystkie firmy";

        if (HistoryCompanyFilter != null)
        {
            var companies = allLogs.Select(log => log.CustomerName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            loadingHistoryFilters = true;
            HistoryCompanyFilter.ItemsSource = new[] { "Wszystkie firmy" }.Concat(companies).ToList();
            HistoryCompanyFilter.SelectedItem = companies.Contains(selectedCompany, StringComparer.CurrentCultureIgnoreCase)
                ? companies.First(name => name.Equals(selectedCompany, StringComparison.CurrentCultureIgnoreCase))
                : "Wszystkie firmy";
            loadingHistoryFilters = false;
        }

        ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter()
    {
        if (LogsGrid == null) return;

        var company = HistoryCompanyFilter?.SelectedItem as string ?? "Wszystkie firmy";
        var statusIndex = HistoryStatusFilter?.SelectedIndex ?? 0;

        var filtered = allLogs.Where(log =>
            (company == "Wszystkie firmy" ||
             log.CustomerName.Equals(company, StringComparison.CurrentCultureIgnoreCase)) &&
            (statusIndex == 0 ||
             statusIndex == 1 && HistoryCategory(log.Status) == "Poprawne" ||
             statusIndex == 2 && HistoryCategory(log.Status) == "Ostrzeżenie" ||
             statusIndex == 3 && HistoryCategory(log.Status) == "Błąd"))
            .ToList();

        LogsGrid.ItemsSource = filtered.Select(log => new
        {
            log.Id,
            LocalTime = log.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            log.CustomerName,
            log.Period,
            Status = HistoryStatusText(log.Status),
            FileCount = log.Status == DeliveryStatus.Success
                ? log.FileCount?.ToString(CultureInfo.InvariantCulture) ?? "—"
                : "—",
            log.Detail
        }).ToList();

        if (HistoryCountText != null)
            HistoryCountText.Text = $"Widoczne: {filtered.Count} / {allLogs.Count}";
    }

    private static string HistoryCategory(DeliveryStatus status) => status switch
    {
        DeliveryStatus.Success => "Poprawne",
        DeliveryStatus.Failed => "Błąd",
        _ => "Ostrzeżenie"
    };

    private static string HistoryStatusText(DeliveryStatus status) => status switch
    {
        DeliveryStatus.Success => "Poprawne",
        DeliveryStatus.Failed => "Błąd",
        DeliveryStatus.Uncertain => "Ostrzeżenie – niepewny",
        DeliveryStatus.Pending => "Ostrzeżenie – w toku",
        _ => "Ostrzeżenie"
    };

    private void HistoryFilterChanged(object senderObject, SelectionChangedEventArgs args)
    {
        if (!loadingHistoryFilters) ApplyHistoryFilter();
    }

    private string SelectedReportCompany()
    {
        var company = HistoryCompanyFilter.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(company) || company == "Wszystkie firmy")
            throw new UserException("W filtrze historii wybierz konkretną firmę, dla której chcesz wygenerować raport.");
        return company;
    }

    private void ExportCompanyReportPdf(object senderObject, RoutedEventArgs args) => Guard(() =>
    {
        var company = SelectedReportCompany();
        var rows = DeliveryReportExporter.SuccessfulForCompany(allLogs, company);
        if (rows.Count == 0) throw new UserException("Brak poprawnych wysyłek tej firmy do ujęcia w raporcie.");

        var dialog = new SaveFileDialog
        {
            Title = "Raport wysyłki PDF",
            Filter = "PDF (*.pdf)|*.pdf",
            FileName = $"raport-wysylki-{SafeFileName(company)}.pdf",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        DeliveryReportExporter.ExportPdf(dialog.FileName, company, allLogs);
        StatusText.Text = $"Wygenerowano raport PDF dla firmy {company}.";
    });

    private void ExportCompanyReportCsv(object senderObject, RoutedEventArgs args) => Guard(() =>
    {
        var company = SelectedReportCompany();
        var rows = DeliveryReportExporter.SuccessfulForCompany(allLogs, company);
        if (rows.Count == 0) throw new UserException("Brak poprawnych wysyłek tej firmy do ujęcia w raporcie.");

        var dialog = new SaveFileDialog
        {
            Title = "Raport wysyłki CSV",
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"raport-wysylki-{SafeFileName(company)}.csv",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        DeliveryReportExporter.ExportCsv(dialog.FileName, company, allLogs);
        StatusText.Text = $"Wygenerowano raport CSV dla firmy {company}.";
    });

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "firma" : result;
    }

    private async void WindowContentRendered(object? senderObject, EventArgs args)
    {
        if (initialUpdateCheckDone) return;
        initialUpdateCheckDone = true;
        try { await CheckForUpdatesAsync(false, CancellationToken.None); }
        catch
        {
            UpdateStatusText.Text = "Nie udało się automatycznie sprawdzić aktualizacji. Możesz użyć przycisku Sprawdź aktualizacje.";
        }
    }

    private async void CheckUpdates(object senderObject, RoutedEventArgs args) =>
        await RunAsync(async cancellationToken => await CheckForUpdatesAsync(true, cancellationToken));

    private async Task CheckForUpdatesAsync(bool showMessages, CancellationToken cancellationToken)
    {
        UpdateStatusText.Text = "Sprawdzanie najnowszej wersji na GitHub…";
        var info = await updater.CheckAsync(cancellationToken);
        LatestVersionText.Text = info.LatestVersion.ToString(3);
        availableUpdate = info.IsUpdateAvailable ? info : null;
        InstallUpdateButton.IsEnabled = info.IsUpdateAvailable;

        if (info.IsUpdateAvailable)
        {
            UpdateStatusText.Text = $"Dostępna jest nowa wersja MP Mail {info.LatestVersion.ToString(3)}.";
            if (showMessages)
                MessageBox.Show(this, $"Dostępna jest nowa wersja MP Mail {info.LatestVersion.ToString(3)}.",
                    "Aktualizacja MP Mail", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            UpdateStatusText.Text = "Masz najnowszą dostępną wersję MP Mail.";
            if (showMessages)
                MessageBox.Show(this, "Masz najnowszą dostępną wersję MP Mail.",
                    "Aktualizacja MP Mail", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void InstallUpdate(object senderObject, RoutedEventArgs args)
    {
        var update = availableUpdate;
        if (update == null)
        {
            await RunAsync(async cancellationToken => await CheckForUpdatesAsync(true, cancellationToken));
            update = availableUpdate;
            if (update == null) return;
        }

        if (MessageBox.Show(this,
                $"Pobrać i zainstalować MP Mail {update.LatestVersion.ToString(3)}?\n\nPo weryfikacji instalatora aplikacja zostanie zamknięta. Po pomyślnej aktualizacji MP Mail uruchomi się automatycznie.",
                "Aktualizacja MP Mail", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        string? installerPath = null;
        await RunAsync(async cancellationToken =>
        {
            installerPath = await updater.DownloadVerifiedInstallerAsync(update,
                new Progress<string>(text =>
                {
                    StatusText.Text = text;
                    UpdateStatusText.Text = text;
                }), cancellationToken);
            UpdateStatusText.Text = "Instalator zweryfikowany. Uruchamianie aktualizacji…";
        });

        if (installerPath == null) return;
        try
        {
            GitHubUpdateService.StartInstaller(installerPath);
            Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void RefreshLogs(object senderObject, RoutedEventArgs args) => Guard(LoadLogs);
    private void CancelOperation(object senderObject, RoutedEventArgs args) => operation?.Cancel();

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (busy) return;
        busy = true;
        Tabs.IsEnabled = false;
        CancelButton.IsEnabled = true;
        operation = new CancellationTokenSource();
        Exception? failure = null;
        try { await action(operation.Token); }
        catch (Exception exception) { failure = exception; }
        finally
        {
            operation.Dispose();
            operation = null;
            busy = false;
            Tabs.IsEnabled = true;
            CancelButton.IsEnabled = false;
            LoadLogs();
            RunQueuedScheduleCheck();
        }
        if (failure != null) ShowError(failure);
    }

    private void Guard(Action action, string? customerName = null)
    {
        try { action(); }
        catch (Exception exception) { ShowError(exception, customerName); }
    }

    private void ShowError(Exception exception, string? customerName = null)
    {
        var message = customerName == null ? ErrorMessages.Describe(exception) : ErrorMessages.ForCustomer(customerName, exception);
        StatusText.Text = message;
        MessageBox.Show(this, message, "MP Mail", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void RequestScheduleCheck()
    {
        scheduleCheckRequested = true;
        scheduleTrace(busy ? "Odebrano żądanie automatu; czeka na zakończenie bieżącej operacji." : "Odebrano żądanie automatu z zadania Windows.");
        ScheduleTick(this, EventArgs.Empty);
    }

    private void RunQueuedScheduleCheck()
    {
        if (scheduleCheckRequested)
            Dispatcher.BeginInvoke(new Action(() => ScheduleTick(this, EventArgs.Empty)));
    }

    private async void ScheduleTick(object? senderObject, EventArgs args) => await CheckScheduleAsync(DateTime.Now);

    public async Task CheckScheduleAsync(DateTime localNow)
    {
        if (busy || !scheduleCheckRequested && localNow < nextScheduleCheck) return;
        scheduleCheckRequested = false;
        nextScheduleCheck = localNow.AddMinutes(1);
        busy = true;
        Tabs.IsEnabled = false;
        CancelButton.IsEnabled = true;
        operation = new CancellationTokenSource();
        try
        {
            if (!repository.Settings().ScheduleEnabled)
            {
                scheduleTrace("Automatyczna wysyłka jest wyłączona.");
                return;
            }
            var previewCleared = false;
            StatusText.Text = "Sprawdzanie reguł wysyłki wg godzin i interwałów…";
            await batches.RunAutomationsAsync(localNow, operation.Token, () =>
            {
                if (prepared == null) return;
                InvalidatePreview();
                previewCleared = true;
                scheduleTrace("Wyczyszczono podgląd przed automatyczną wysyłką, aby nie blokował harmonogramu i nie umożliwiał wysłania nieaktualnego ZIP.");
            }, scheduleTrace);
            StatusText.Text = previewCleared
                ? "Automat wyczyścił nieaktualny podgląd i wykonał harmonogram. Wyniki są w historii."
                : "Sprawdzono harmonogramy. Wyniki są w historii.";
        }
        catch (Exception exception)
        {
            scheduleTrace($"Błąd automatu: {ErrorMessages.Describe(exception)}");
            repository.Log(new Customer { Name = "Harmonogram" }, DateTime.Now.ToString("yyyy-MM-dd"),
                DeliveryStatus.Failed, ErrorMessages.Describe(exception));
            StatusText.Text = ErrorMessages.Describe(exception);
        }
        finally
        {
            operation.Dispose();
            operation = null;
            busy = false;
            Tabs.IsEnabled = true;
            CancelButton.IsEnabled = false;
            LoadLogs();
            RunQueuedScheduleCheck();
        }
    }

    private void OnClosing(object? senderObject, CancelEventArgs args)
    {
        if (busy)
        {
            args.Cancel = true;
            operation?.Cancel();
            StatusText.Text = "Anulowanie operacji. Zamknij aplikację po jej zakończeniu.";
            return;
        }
        timer.Stop();
        prepared?.Dispose();
    }
}
