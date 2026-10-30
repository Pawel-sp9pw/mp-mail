using System.Globalization;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace KsgMail.Core;

public static class DeliveryReportExporter
{
    public static IReadOnlyList<DeliveryLog> SuccessfulForCompany(IEnumerable<DeliveryLog> logs, string companyName) =>
        logs.Where(log =>
                log.Status == DeliveryStatus.Success &&
                !string.IsNullOrWhiteSpace(log.MessageId) &&
                log.CustomerName.Equals(companyName, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(log => log.Timestamp)
            .ToList();

    public static void ExportCsv(string path, string companyName, IEnumerable<DeliveryLog> logs)
    {
        var rows = SuccessfulForCompany(logs, companyName);
        var builder = new StringBuilder();
        builder.AppendLine("firma;data;godzina;okres;liczba_orzeczen;informacja");

        foreach (var log in rows)
        {
            var local = log.Timestamp.ToLocalTime();
            builder.Append(Csv(companyName)).Append(';')
                .Append(local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(';')
                .Append(local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append(';')
                .Append(Csv(log.Period)).Append(';')
                .Append(log.FileCount?.ToString(CultureInfo.InvariantCulture) ?? "").Append(';')
                .Append(Csv(log.Detail)).AppendLine();
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    public static void ExportPdf(string path, string companyName, IEnumerable<DeliveryLog> logs)
    {
        var rows = SuccessfulForCompany(logs, companyName);
        var fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        if (!File.Exists(fontPath))
            throw new UserException("Nie znaleziono systemowej czcionki Arial wymaganej do wygenerowania raportu PDF.");

        var builder = new PdfDocumentBuilder();
        var font = builder.AddTrueTypeFont(File.ReadAllBytes(fontPath));
        var page = builder.AddPage(PageSize.A4);
        double y = 800;

        void Line(string text, double size = 9, double x = 40)
        {
            if (y < 55)
            {
                page = builder.AddPage(PageSize.A4);
                y = 800;
            }

            page.AddText(text, size, new PdfPoint(x, y), font);
            y -= size + 6;
        }

        Line("MP Mail - raport wysyłki orzeczeń", 15);
        Line($"Firma: {companyName}", 11);
        Line($"Wygenerowano: {DateTime.Now:yyyy-MM-dd HH:mm:ss}", 9);
        y -= 8;
        Line("Data       Godzina   Okres                         Liczba orzeczeń", 9);
        Line(new string('-', 78), 8);

        foreach (var log in rows)
        {
            var local = log.Timestamp.ToLocalTime();
            var count = log.FileCount?.ToString(CultureInfo.InvariantCulture) ?? "brak danych";
            var period = log.Period.Length > 28 ? log.Period[..28] : log.Period;
            Line($"{local:yyyy-MM-dd}  {local:HH:mm:ss}  {period,-28}  {count}", 9);
        }

        y -= 8;
        var known = rows.Where(row => row.FileCount.HasValue).Sum(row => row.FileCount!.Value);
        var unknown = rows.Count(row => !row.FileCount.HasValue);
        Line($"Liczba udanych wysyłek: {rows.Count}", 10);
        Line($"Łączna liczba orzeczeń z wpisów posiadających dane: {known}", 10);
        if (unknown > 0)
            Line($"Starsze wpisy bez zapisanej liczby orzeczeń: {unknown}", 9);

        File.WriteAllBytes(path, builder.Build());
    }

    private static string Csv(string value)
    {
        if (!value.Contains(';') && !value.Contains('"') && !value.Contains('\r') && !value.Contains('\n'))
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
