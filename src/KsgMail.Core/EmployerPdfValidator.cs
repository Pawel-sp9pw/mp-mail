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

        const string startPhrase = "do pracy w";
        var normalizedStart = Normalize(startPhrase);
        var normalizedLabel = Normalize(EmployerLabel);

        for (var index = 0; index < lines.Count; index++)
        {
            var normalizedLine = Normalize(lines[index]);
            var startIndex = normalizedLine.IndexOf(normalizedStart, StringComparison.Ordinal);
            if (startIndex < 0) continue;

            var valueParts = new List<string>();
            var originalLine = lines[index];
            var phraseIndex = originalLine.IndexOf(startPhrase, StringComparison.OrdinalIgnoreCase);
            if (phraseIndex >= 0)
            {
                var remainder = originalLine[(phraseIndex + startPhrase.Length)..].Trim();
                if (remainder.Length > 0) valueParts.Add(remainder);
            }

            for (var next = index + 1; next < lines.Count; next++)
            {
                if (Normalize(lines[next]).Contains(normalizedLabel, StringComparison.Ordinal))
                    break;
                valueParts.Add(lines[next]);
            }

            var value = string.Join(" ", valueParts).Trim();
            if (value.Length == 0) continue;

            var comma = value.IndexOf(',');
            var name = (comma >= 0 ? value[..comma] : value).Trim();
            if (name.Length > 0) return name;
        }

        return null;
    }

    public static bool CompanyNamesEqual(string left, string right) =>
        NormalizeCompanyName(left).Equals(NormalizeCompanyName(right), StringComparison.Ordinal);

    private static string NormalizeCompanyName(string value)
    {
        var normalized = Normalize(value);

        // Jednoznaczne odpowiedniki form prawnych. PDF-y potrafią zawierać w warstwie tekstowej
        // pełną nazwę formy prawnej, mimo że wizualnie wydrukowany jest skrót.
        normalized = Regex.Replace(normalized,
            @"\bSPÓŁKA\s+Z\s+OGRANICZONĄ\s+ODPOWIEDZIALNOŚCIĄ\b",
            "SP Z OO", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized,
            @"\bSP[.]?\s*Z[.]?\s*O[.]?\s*O[.]?\b",
            "SP Z OO", RegexOptions.CultureInvariant);

        normalized = Regex.Replace(normalized,
            @"\bSPÓŁKA\s+AKCYJNA\b",
            "SA", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized,
            @"\bS[.]?\s*A[.]?\b",
            "SA", RegexOptions.CultureInvariant);

        normalized = Regex.Replace(normalized, @"[.,;:]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized;
    }

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized.ToUpperInvariant();
    }
}
