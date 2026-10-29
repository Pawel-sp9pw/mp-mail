using System.IO;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace KsgMail.Tests;

internal static class TestPdf
{
    public static void WriteMedicalCertificate(string path, string employerName)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText("zatrudniony do pracy w", 10, new PdfPoint(40, 720), font);
        page.AddText($"{employerName}, Testowa 1, 00-001 Warszawa", 10, new PdfPoint(40, 700), font);
        page.AddText("(nazwa i adres pracodawcy)", 9, new PdfPoint(40, 680), font);
        page.AddText("orzeczenie testowe", 10, new PdfPoint(40, 640), font);
        File.WriteAllBytes(path, builder.Build());
    }
}
