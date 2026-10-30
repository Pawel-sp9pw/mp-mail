namespace KsgMail.Core;

public sealed class BatchService(Repository repository, ArchiveService archives, IMailSender sender, string tempRoot)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<PreparedBatch> PrepareAsync(IEnumerable<Customer> customers, AppSettings settings, string period,
        bool allowResend, IProgress<string>? progress, CancellationToken cancellationToken, bool negativeOnly = false,
        string? dispatchGroup = null, DateOnly? sendDate = null)
    {
        Validation.Settings(settings);
        var templatePeriod = TemplatePeriod(period);
        var group = dispatchGroup ?? (negativeOnly ? "interval:15" : "scheduled");
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
                progress?.Report($"Sprawdzanie: {customer.Name}");
                var effectiveSendDate = sendDate ?? DateOnly.FromDateTime(DateTime.Now);
                var subject = Templates.Render(customer.Subject, customer, templatePeriod, effectiveSendDate);
                var body = Templates.Render(customer.Body, customer, templatePeriod, effectiveSendDate);
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
                    var files = await Task.Run(() => archives.Discover(customer.SourceDirectory,
                        settings.MaxSourceMb * 1048576L, settings.FileRules,
                        rule => RuleMatchesGroup(rule, group), GroupDescription(group)), cancellationToken);
                    if (files.Count == 0)
                    {
                        batch.Items.Add(new(customer, period, subject, body, "", [], 0,
                            "Katalog nie zawiera plików do wysłania.", true));
                        continue;
                    }
                    await Task.Run(() => EmployerPdfValidator.ValidateFiles(files, customer.Name), cancellationToken);
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

    public async Task SendAsync(PreparedBatch batch, bool allowResend, IProgress<string>? progress,
        CancellationToken cancellationToken, bool notifyErrors = true)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) throw new UserException("Trwa już wysyłka.");
        try
        {
            foreach (var mail in batch.Items)
            {
                if (cancellationToken.IsCancellationRequested) break;
                progress?.Report($"Sprawdzanie: {mail.Name}");
                if (mail.Skipped)
                {
                    repository.Log(mail.Customer, mail.Period, DeliveryStatus.Skipped, mail.Status);
                    continue;
                }
                if (!mail.Ready)
                {
                    repository.Log(mail.Customer, mail.Period, DeliveryStatus.Failed, mail.Error!);
                    if (notifyErrors)
                        await NotifyErrorSafeAsync(batch.Settings, mail.Customer, mail.Period,
                            DeliveryStatus.Failed, mail.Error!, cancellationToken, progress);
                    continue;
                }
                var messageId = SmtpSender.NewMessageId();
                if (!repository.Reserve(mail.Customer, mail.Period, messageId, allowResend))
                {
                    repository.Log(mail.Customer, mail.Period, DeliveryStatus.Skipped, "Blokada ponownej wysyłki.");
                    continue;
                }
                progress?.Report($"Wysyłka: {mail.Name}");
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
                repository.Finish(mail.Customer, mail.Period, outcome.Status, detail, messageId,
                    outcome.Status == DeliveryStatus.Success ? mail.Files.Count : null);
                if (notifyErrors && (outcome.Status is DeliveryStatus.Failed or DeliveryStatus.Uncertain))
                    await NotifyErrorSafeAsync(batch.Settings, mail.Customer, mail.Period,
                        outcome.Status, detail, cancellationToken, progress);
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

        await RunScheduledAsync(localNow, cancellationToken, beforeSend, diagnostics);

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

        var intervals = settings.FileRules
            .Where(rule => rule.Enabled && rule.Mode == FileDispatchMode.Interval)
            .Select(rule => rule.IntervalMinutes)
            .Distinct()
            .Order()
            .ToList();

        foreach (var interval in intervals)
        {
            var totalMinutes = localNow.Hour * 60 + localNow.Minute;
            var slotMinutes = totalMinutes - totalMinutes % interval;
            var slotHour = slotMinutes / 60;
            var slotMinute = slotMinutes % 60;
            var slot = new DateTime(localNow.Year, localNow.Month, localNow.Day, slotHour, slotMinute, 0);
            var intervalRules = settings.FileRules
                .Where(rule => rule.Enabled && rule.Mode == FileDispatchMode.Interval && rule.IntervalMinutes == interval)
                .ToList();
            var legacyNegativeGroup = intervalRules.Count == 1 &&
                interval == settings.NegativeIntervalMinutes &&
                intervalRules[0].Pattern.Equals("NEG.orzeczenie-profilaktyczne-p1*.pdf", StringComparison.OrdinalIgnoreCase);
            var period = legacyNegativeGroup
                ? $"NEG-{slot:yyyy-MM-dd-HHmm}"
                : $"INT-{interval}-{slot:yyyy-MM-dd-HHmm}";
            var group = $"interval:{interval}";
            var customers = ScheduledCustomers(period, null, group, diagnostics);
            if (customers.Count == 0) continue;

            diagnostics?.Invoke($"Interwał {interval} min: rozpoczęto partię dla {customers.Count} firm.");
            beforeSend?.Invoke();
            using var batch = await PrepareAsync(customers, settings, period, false, null, cancellationToken,
                dispatchGroup: group, sendDate: DateOnly.FromDateTime(localNow));
            await SendAsync(batch, false, null, cancellationToken);
        }
        diagnostics?.Invoke("Zakończono sprawdzanie automatu. Wyniki prób wysyłki są w historii.");
    }

    public async Task RunScheduledAsync(DateTime localNow, CancellationToken cancellationToken,
        Action? beforeSend = null, Action<string>? diagnostics = null)
    {
        var settings = repository.Settings();
        if (!settings.IsDue(localNow))
        {
            diagnostics?.Invoke("Wysyłka zbiorcza pominięta: poza ustawioną minutą lub harmonogram wyłączony. Zaległe terminy nie są nadrabiane.");
            return;
        }
        var period = settings.DueAt(localNow).TimeOfDay == settings.GetScheduleTimes()[0].ToTimeSpan()
            ? localNow.ToString("yyyy-MM-dd")
            : $"SCHED-{settings.DueAt(localNow):yyyy-MM-dd-HHmm}";
        cancellationToken.ThrowIfCancellationRequested();
        if (!repository.TryStartScheduledBatch(period))
        {
            diagnostics?.Invoke($"Termin {period}: partia została już rozpoczęta. Kolejne pliki lub firmy czekają na następny termin.");
            return;
        }
        var customers = ScheduledCustomers(period, localNow.ToString("yyyy-MM-dd"), "scheduled", diagnostics);
        if (customers.Count == 0) return;
        diagnostics?.Invoke($"Rozpoczęto jedną partię zbiorczą dla {customers.Count} firm w terminie {period}.");
        beforeSend?.Invoke();
        using var batch = await PrepareAsync(customers, settings, period, false, null, cancellationToken,
            sendDate: DateOnly.FromDateTime(localNow));
        await SendAsync(batch, false, null, cancellationToken);
    }

    private List<Customer> ScheduledCustomers(string period, string? day, string dispatchGroup, Action<string>? diagnostics)
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
                if (!Directory.Exists(customer.SourceDirectory))
                {
                    selected.Add(customer);
                    continue;
                }

                if (archives.HasMatchingFiles(customer.SourceDirectory, repository.Settings().FileRules,
                        rule => RuleMatchesGroup(rule, dispatchGroup)))
                {
                    selected.Add(customer);
                }
                else
                {
                    noFiles++;
                    if (dispatchGroup == "scheduled" && !Directory.EnumerateFiles(customer.SourceDirectory).Any())
                        repository.Log(customer, period, DeliveryStatus.Skipped,
                            "Katalog nie zawiera plików do wysłania.");
                }
            }
            catch (Exception exception)
            {
                errors++;
                diagnostics?.Invoke(ErrorMessages.ForCustomer(customer.Name, exception));
                // Przekazujemy firmę dalej: PrepareAsync zapisze dokładny błąd,
                // a SendAsync w trybie harmonogramu wyśle powiadomienie o błędzie.
                selected.Add(customer);
            }
        }
        diagnostics?.Invoke($"Termin {period}: do wysyłki {selected.Count}; nieaktywne {inactive}; już wysłane lub wynik niepewny {blocked}; brak pasujących plików lub niedostępny katalog {noFiles}; błędy {errors}.");
        return selected;
    }
    private static bool RuleMatchesGroup(FileDispatchRule rule, string group) =>
        group == "scheduled"
            ? rule.Mode == FileDispatchMode.Scheduled
            : group.StartsWith("interval:", StringComparison.Ordinal) &&
              rule.Mode == FileDispatchMode.Interval &&
              int.TryParse(group.AsSpan("interval:".Length), out var minutes) &&
              rule.IntervalMinutes == minutes;

    private static string GroupDescription(string group) =>
        group == "scheduled" ? "wysyłka wg godzin" :
        group.StartsWith("interval:", StringComparison.Ordinal) ? $"interwał {group["interval:".Length..]} min" : group;

    private async Task NotifyErrorSafeAsync(AppSettings settings, Customer customer, string period,
        DeliveryStatus status, string detail, CancellationToken cancellationToken, IProgress<string>? progress)
    {
        if (string.IsNullOrWhiteSpace(settings.ErrorNotificationEmail)) return;
        try
        {
            await sender.NotifyErrorAsync(settings, customer, period, status, detail, cancellationToken);
        }
        catch (Exception exception)
        {
            progress?.Report($"{customer.Name}: nie udało się wysłać powiadomienia o błędzie: {ErrorMessages.Describe(exception)}");
        }
    }

    private static string TemplatePeriod(string period)
    {
        if (period.Length == 7 && DateOnly.TryParseExact(period + "-01", "yyyy-MM-dd", out _))
            return period;
        for (var index = 0; index + 10 <= period.Length; index++)
        {
            var candidate = period.Substring(index, 10);
            if (DateOnly.TryParseExact(candidate, "yyyy-MM-dd", out _))
                return candidate;
        }
        return period;
    }

}
