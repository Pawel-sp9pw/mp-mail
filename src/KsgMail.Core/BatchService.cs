namespace KsgMail.Core;

public sealed class BatchService(Repository repository, ArchiveService archives, IMailSender sender, string tempRoot)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<PreparedBatch> PrepareAsync(IEnumerable<Customer> customers, AppSettings settings, string period,
        bool allowResend, IProgress<string>? progress, CancellationToken cancellationToken, bool negativeOnly = false)
    {
        Validation.Settings(settings);
        var templatePeriod = period.StartsWith("NEG-", StringComparison.Ordinal) ? period.Substring(4, 10) :
            period.StartsWith("SCHED-", StringComparison.Ordinal) ? period.Substring(6, 10) : period;
        var validMonth = DateOnly.TryParseExact(templatePeriod + "-01", "yyyy-MM-dd", out _);
        var validDay = DateOnly.TryParseExact(templatePeriod, "yyyy-MM-dd", out _);
        if (!validMonth && !validDay) throw new UserException("Niepoprawny okres wysyłki.");
        var directory = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var batch = new PreparedBatch(settings, directory);
        try
        {
            foreach (var customer in customers.Where(customer => customer.Active))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Przygotowanie: {customer.Name}");
                var subject = Templates.Render(customer.Subject, customer, templatePeriod);
                var body = Templates.Render(customer.Body, customer, templatePeriod);
                var path = Path.Combine(directory, customer.Id + ".zip");
                if (!allowResend && repository.IsBlocked(customer.Id, period))
                {
                    batch.Items.Add(new(customer, period, subject, body, "", [], 0, null, true));
                    continue;
                }
                try
                {
                    Validation.Customer(customer);
                    var sourcePath = Path.GetFullPath(customer.SourceDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    var tempPath = Path.GetFullPath(tempRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (tempPath.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase) ||
                        sourcePath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase))
                        throw new UserException("Katalog dokumentów nie może obejmować katalogu roboczego MP Mail.");
                    var files = await Task.Run(() => archives.Discover(customer.SourceDirectory, settings.MaxSourceMb * 1048576L, negativeOnly), cancellationToken);
                    await Task.Run(() => archives.Create(path, files, customer, settings.MaxAttachmentMb * 1048576L, cancellationToken), cancellationToken);
                    batch.Items.Add(new(customer, period, subject, body, path, files, new FileInfo(path).Length, null, false));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    if (File.Exists(path)) File.Delete(path);
                    batch.Items.Add(new(customer, period, subject, body, "", [], 0, ErrorMessages.Describe(exception), false));
                }
            }
            return batch;
        }
        catch { batch.Dispose(); throw; }
    }

    public async Task SendAsync(PreparedBatch batch, bool allowResend, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) throw new UserException("Trwa już wysyłka.");
        try
        {
            foreach (var mail in batch.Items)
            {
                if (cancellationToken.IsCancellationRequested) break;
                progress?.Report($"Wysyłka: {mail.Name}");
                if (mail.Skipped)
                {
                    repository.Log(mail.Customer, mail.Period, DeliveryStatus.Skipped, mail.Status);
                    continue;
                }
                if (!mail.Ready)
                {
                    repository.Log(mail.Customer, mail.Period, DeliveryStatus.Failed, mail.Error!);
                    continue;
                }
                var messageId = SmtpSender.NewMessageId();
                if (!repository.Reserve(mail.Customer, mail.Period, messageId, allowResend))
                {
                    repository.Log(mail.Customer, mail.Period, DeliveryStatus.Skipped, "Blokada ponownej wysyłki.");
                    continue;
                }
                SendOutcome outcome;
                try { outcome = await sender.SendAsync(batch.Settings, mail, messageId, cancellationToken); }
                catch (Exception)
                {
                    outcome = new(DeliveryStatus.Uncertain, "Nie udało się ustalić wyniku wysyłki. Sprawdź u odbiorcy przed ponowieniem.");
                }
                var detail = outcome.Detail;
                if (outcome.Status == DeliveryStatus.Success)
                {
                    try
                    {
                        archives.ArchiveSent(mail.Customer.SourceDirectory, mail.Files, mail.Period);
                        detail += " Pliki przeniesiono do archiwum.";
                    }
                    catch (Exception exception)
                    {
                        detail += $" Wysłano, ale archiwizacja plików nie powiodła się: {ErrorMessages.Describe(exception)}";
                    }
                }
                repository.Finish(mail.Customer, mail.Period, outcome.Status, detail, messageId);
                progress?.Report($"{mail.Name}: {detail}");
            }
        }
        finally { gate.Release(); }
    }

    public async Task RunAutomationsAsync(DateTime localNow, CancellationToken cancellationToken,
        Action? beforeSend = null, Action<string>? diagnostics = null)
    {
        var settings = repository.Settings();
        if (!settings.ScheduleEnabled)
        {
            diagnostics?.Invoke("Automatyczna wysyłka jest wyłączona.");
            return;
        }
        diagnostics?.Invoke($"Sprawdzanie automatu: {localNow:yyyy-MM-dd HH:mm:ss}; godziny {settings.ScheduleTime}.");

        foreach (var customer in repository.Customers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var deleted = archives.PurgeExpired(customer.SourceDirectory, settings.ArchiveRetentionDays, DateTime.UtcNow);
                if (deleted > 0)
                    repository.Log(customer, localNow.ToString("yyyy-MM-dd"), DeliveryStatus.Success,
                        $"Retencja archiwum: usunięto {deleted} plików starszych niż {settings.ArchiveRetentionDays} dni.");
            }
            catch (Exception exception)
            {
                repository.Log(customer, localNow.ToString("yyyy-MM-dd"), DeliveryStatus.Failed,
                    $"Retencja archiwum nie powiodła się: {ErrorMessages.Describe(exception)}");
            }
        }

        await RunScheduledAsync(localNow, cancellationToken, beforeSend, diagnostics);

        var interval = settings.NegativeIntervalMinutes;
        var totalMinutes = localNow.Hour * 60 + localNow.Minute;
        var slotMinutes = totalMinutes - totalMinutes % interval;
        var slotHour = slotMinutes / 60;
        var slotMinute = slotMinutes % 60;
        var slot = new DateTime(localNow.Year, localNow.Month, localNow.Day, slotHour, slotMinute, 0);
        var negativePeriod = $"NEG-{slot:yyyy-MM-dd-HHmm}";
        var negativeCustomers = ScheduledCustomers(negativePeriod, null, true, diagnostics);
        if (negativeCustomers.Count > 0)
        {
            beforeSend?.Invoke();
            using var negativeBatch = await PrepareAsync(negativeCustomers, settings, negativePeriod, false, null, cancellationToken, true);
            await SendAsync(negativeBatch, false, null, cancellationToken);
        }
        diagnostics?.Invoke("Zakończono sprawdzanie automatu. Wyniki prób wysyłki są w historii.");
    }

    public async Task RunScheduledAsync(DateTime localNow, CancellationToken cancellationToken,
        Action? beforeSend = null, Action<string>? diagnostics = null)
    {
        var settings = repository.Settings();
        if (!settings.IsDue(localNow))
        {
            diagnostics?.Invoke("Wysyłka zbiorcza: przed pierwszym terminem lub harmonogram wyłączony.");
            return;
        }
        var period = settings.DueAt(localNow).TimeOfDay == settings.GetScheduleTimes()[0].ToTimeSpan()
            ? localNow.ToString("yyyy-MM-dd")
            : $"SCHED-{settings.DueAt(localNow):yyyy-MM-dd-HHmm}";
        var customers = ScheduledCustomers(period, localNow.ToString("yyyy-MM-dd"), false, diagnostics);
        if (customers.Count == 0) return;
        beforeSend?.Invoke();
        using var batch = await PrepareAsync(customers, settings, period, false, null, cancellationToken);
        await SendAsync(batch, false, null, cancellationToken);
    }

    private List<Customer> ScheduledCustomers(string period, string? day, bool negativeOnly, Action<string>? diagnostics)
    {
        var selected = new List<Customer>();
        int inactive = 0, blocked = 0, noFiles = 0, errors = 0;
        foreach (var customer in repository.Customers())
        {
            if (!customer.Active) { inactive++; continue; }
            if (repository.IsBlocked(customer.Id, period) ||
                day != null && repository.HasUnresolvedScheduledDelivery(customer.Id, day))
            {
                blocked++;
                continue;
            }
            try
            {
                if (archives.HasMatchingFiles(customer.SourceDirectory, negativeOnly)) selected.Add(customer);
                else noFiles++;
            }
            catch (Exception exception)
            {
                errors++;
                var detail = ErrorMessages.ForCustomer(customer.Name, exception);
                repository.Log(customer, period, DeliveryStatus.Failed, detail);
                diagnostics?.Invoke(detail);
            }
        }
        diagnostics?.Invoke($"Termin {period}: do wysyłki {selected.Count}; nieaktywne {inactive}; już wysłane lub wynik niepewny {blocked}; brak pasujących plików lub niedostępny katalog {noFiles}; błędy {errors}.");
        return selected;
    }
}
