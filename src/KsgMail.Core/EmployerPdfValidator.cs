using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace KsgMail.Core;

public static class EmployerPdfValidator
{
    private const string EmployerLabel = "nazwa i adres pracodawcy";

    public static void ValidateFiles(IReadOnlyList<SourceFile> files, string expectedCompanyName)
    {
        foreach (var file in files.Where(file =>
                     Path.GetExtension(file.FullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)))
        {
            var employer = ReadEmployerName(file.FullPath);
            if (!CompanyNamesEqual(employer, expectedCompanyName))
                throw new UserException(
                    $"Plik „{file.RelativePath}”: pracodawca w PDF „{employer}” nie zgadza się z firmą w bazie „{expectedCompanyName}”.");
        }
    }

    public static string ReadEmployerName(string pdfPath)
    {
        try
        {
            using var document = PdfDocument.Open(pdfPath);
            foreach (var page in document.GetPages())
            {
                var text = ContentOrderTextExtractor.GetText(page, true);
                var employer = ExtractEmployerNameFromText(text);
                if (employer != null) return employer;
            }
        }
        catch (UserException) { throw; }
        catch (Exception exception)
        {
            throw new UserException(
                $"Nie można odczytać pola pracodawcy z PDF „{Path.GetFileName(pdfPath)}”.", exception);
        }

        throw new UserException(
            $"PDF „{Path.GetFileName(pdfPath)}” nie zawiera rozpoznawalnego pola „nazwa i adres pracodawcy”.");
    }

    public static string? ExtractEmployerNameFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r", "")
            .Split('\n')
            .Select(line => Regex.Replace(line.Trim(), @"\s+", " "))
            .Where(line => line.Length > 0)
            .ToList();

        for (var index = 0; index < lines.Count; index++)
        {
            if (!Normalize(lines[index]).Contains(Normalize(EmployerLabel), StringComparison.Ordinal))
                continue;

            for (var previous = index - 1; previous >= 0; previous--)
            {
                var candidate = lines[previous].Trim();
                if (candidate.Length == 0) continue;
                var comma = candidate.IndexOf(',');
                var name = (comma >= 0 ? candidate[..comma] : candidate).Trim();
                if (name.Length > 0) return name;
            }
        }

        return null;
    }

    public static bool CompanyNamesEqual(string left, string right) =>
        Normalize(left).Equals(Normalize(right), StringComparison.Ordinal);

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized.ToUpperInvariant();
    }
}
