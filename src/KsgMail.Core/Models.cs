using System.Globalization;
using MimeKit;

namespace KsgMail.Core;

public sealed record Customer
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public string Subject { get; init; } = "Dokumenty za {miesiac}";
    public string Body { get; init; } = "Dzień dobry,\n\nw załączeniu dokumenty za {miesiac}.\n\nPozdrawiamy";
    public string SourceDirectory { get; init; } = "";
    public string ZipPasswordProtected { get; init; } = "";
    public bool Active { get; init; } = true;
}

public enum TransportSecurity { StartTls, SslOnConnect }
public enum DeliveryStatus { Pending, Success, Failed, Uncertain, Skipped }
public enum FileDispatchMode { Scheduled, Interval }

public sealed class FileDispatchRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Pattern { get; set; } = "*.pdf";
    public bool Enabled { get; set; } = true;
    public FileDispatchMode Mode { get; set; } = FileDispatchMode.Scheduled;
    public int IntervalMinutes { get; set; } = 15;

    public FileDispatchRule Clone() => new()
    {
        Id = Id,
        Pattern = Pattern,
        Enabled = Enabled,
        Mode = Mode,
        IntervalMinutes = IntervalMinutes
    };
}

public sealed record AppSettings
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 587;
    public TransportSecurity Security { get; init; } = TransportSecurity.StartTls;
    public string Username { get; init; } = "";
    public string PasswordProtected { get; init; } = "";
    public string SenderEmail { get; init; } = "";
    public string SenderName { get; init; } = "Medycyna pracy";
    public int MaxAttachmentMb { get; init; } = 20;
    public int MaxSourceMb { get; init; } = 500;
    public int RetryCount { get; init; } = 3;
    public bool ScheduleEnabled { get; init; }
    public string ScheduleTime { get; init; } = "09:00";
    public int NegativeIntervalMinutes { get; init; } = 15;
    public int ArchiveRetentionDays { get; init; } = 90;
    public IReadOnlyList<FileDispatchRule> FileRules { get; init; } =
    [
        new()
        {
            Pattern = "orzeczenie-profilaktyczne-p1*.pdf",
            Enabled = true,
            Mode = FileDispatchMode.Scheduled,
            IntervalMinutes = 15
        },
        new()
        {
            Pattern = "NEG.orzeczenie-profilaktyczne-p1*.pdf",
            Enabled = true,
            Mode = FileDispatchMode.Interval,
            IntervalMinutes = 15
        }
    ];

    public IReadOnlyList<TimeOnly> GetScheduleTimes()
    {
        var values = ScheduleTime.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (values.Length == 0) throw new UserException("Podaj co najmniej jedną godzinę wysyłki w formacie HH:mm.");
        var times = new List<TimeOnly>();
        foreach (var value in values)
        {
            if (!TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                throw new UserException($"Niepoprawna godzina „{value}”. Użyj HH:mm, np. 09:00, 13:00, 16:30.");
            times.Add(time);
        }
        return times.Distinct().Order().ToList();
    }

    public DateTime DueAt(DateTime localNow)
    {
        var times = GetScheduleTimes();
        var time = times.Where(value => value <= TimeOnly.FromDateTime(localNow)).DefaultIfEmpty(times[0]).Last();
        return new(localNow.Year, localNow.Month, localNow.Day, time.Hour, time.Minute, 0);
    }

    public bool IsDue(DateTime localNow) => ScheduleEnabled &&
        GetScheduleTimes().Any(time => time.Hour == localNow.Hour && time.Minute == localNow.Minute);
}

public sealed record DeliveryLog(long Id, DateTimeOffset Timestamp, string CustomerName,
    string Email, string Period, DeliveryStatus Status, string Detail, string MessageId);

public sealed record SourceFile(string FullPath, string RelativePath, long Length, DateTime LastWriteUtc);

public static class Validation
{
    public static void Customer(Customer customer)
    {
        Require(Guid.TryParseExact(customer.Id, "N", out _), "Niepoprawny identyfikator firmy.");
        Require(!string.IsNullOrWhiteSpace(customer.Name), "Podaj nazwę firmy.");
        if (customer.Active || !string.IsNullOrWhiteSpace(customer.Email))
            Email(customer.Email, "Adres firmy");
        Require(!string.IsNullOrWhiteSpace(customer.Subject), "Podaj temat wiadomości.");
        Require(!customer.Subject.Contains('\r') && !customer.Subject.Contains('\n'), "Temat nie może zawierać nowych linii.");
        Require(!string.IsNullOrWhiteSpace(customer.Body), "Podaj treść wiadomości.");
        Require(Path.IsPathFullyQualified(customer.SourceDirectory), "Wybierz bezwzględną ścieżkę katalogu.");
        if (customer.Active)
            Require(!string.IsNullOrEmpty(customer.ZipPasswordProtected), "Ustaw hasło ZIP.");
    }

    public static void Settings(AppSettings settings)
    {
        Require(!string.IsNullOrWhiteSpace(settings.Host) && !settings.Host.Any(char.IsWhiteSpace), "Podaj poprawny host SMTP.");
        Require(settings.Port is >= 1 and <= 65535, "Port SMTP: 1–65535.");
        Require(Enum.IsDefined(settings.Security), "Nieprawidłowy rodzaj TLS.");
        Require(!string.IsNullOrWhiteSpace(settings.Username), "Podaj login SMTP.");
        Require(!string.IsNullOrEmpty(settings.PasswordProtected), "Ustaw hasło SMTP.");
        Email(settings.SenderEmail, "Adres nadawcy");
        Require(!settings.SenderName.Contains('\r') && !settings.SenderName.Contains('\n'), "Nazwa nadawcy nie może zawierać nowych linii.");
        Require(settings.MaxAttachmentMb is >= 1 and <= 100, "Limit ZIP: 1–100 MB.");
        Require(settings.MaxSourceMb is >= 1 and <= 2048, "Limit plików źródłowych: 1–2048 MB.");
        Require(settings.RetryCount is >= 1 and <= 5, "Liczba prób SMTP: 1–5.");
        settings.GetScheduleTimes();
        Require(settings.NegativeIntervalMinutes is >= 1 and <= 1440, "Interwał NEG: 1–1440 minut.");
        Require(settings.ArchiveRetentionDays is >= 1 and <= 3650, "Retencja archiwum: 1–3650 dni.");
        var rules = settings.FileRules ?? [];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            Require(!string.IsNullOrWhiteSpace(rule.Id) && ids.Add(rule.Id), "Reguły plików muszą mieć unikalne identyfikatory.");
            Require(!string.IsNullOrWhiteSpace(rule.Pattern), "Reguła plików: podaj nazwę lub wzorzec pliku.");
            Require(!rule.Pattern.Contains('/') && !rule.Pattern.Contains('\\'),
                $"Reguła „{rule.Pattern}”: podaj samą nazwę/wzorzec pliku bez ścieżki.");
            Require(Enum.IsDefined(rule.Mode), $"Reguła „{rule.Pattern}”: nieprawidłowy tryb wysyłki.");
            if (rule.Mode == FileDispatchMode.Interval)
                Require(rule.IntervalMinutes is >= 1 and <= 1440,
                    $"Reguła „{rule.Pattern}”: interwał musi mieć 1–1440 minut.");
        }
    }

    public static void Email(string value, string label)
    {
        Require(!value.Contains('\r') && !value.Contains('\n') &&
            MailboxAddress.TryParse(value, out var parsed) && parsed.Address == value &&
            value.Contains('@'), $"{label}: podaj jeden poprawny adres e-mail.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new UserException(message);
    }
}

public sealed class UserException(string message, Exception? innerException = null) : Exception(message, innerException);

public static class Templates
{
    public static string Render(string text, Customer customer, string period)
    {
        var month = period.Length >= 7 ? period[..7] : period;
        var date = period.Length == 10 ? period : period + "-01";
        return text.Replace("{klient}", customer.Name, StringComparison.Ordinal)
            .Replace("{miesiac}", month, StringComparison.Ordinal)
            .Replace("{data}", date, StringComparison.Ordinal);
    }
}
