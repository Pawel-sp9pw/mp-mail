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

    public DateTime DueAt(DateTime localNow)
    {
        var time = TimeOnly.ParseExact(ScheduleTime, "HH:mm", CultureInfo.InvariantCulture);
        return new(localNow.Year, localNow.Month, localNow.Day, time.Hour, time.Minute, 0);
    }

    public bool IsDue(DateTime localNow) => ScheduleEnabled && localNow >= DueAt(localNow);
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
        Require(TimeOnly.TryParseExact(settings.ScheduleTime, "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _), "Godzina musi mieć format HH:mm.");
        Require(settings.NegativeIntervalMinutes is >= 1 and <= 1440, "Interwał NEG: 1–1440 minut.");
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

public sealed class UserException(string message) : Exception(message);

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
