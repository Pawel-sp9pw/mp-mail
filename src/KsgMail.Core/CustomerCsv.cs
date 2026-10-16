using System.Text;

namespace KsgMail.Core;

public sealed record CsvImportResult(int Added, int Updated, IReadOnlyList<Customer> Customers);

public static class CustomerCsv
{
    public const string Header = "id;nazwa;email;temat;tresc;katalog;haslo_zip;aktywny";

    public static void Export(string path, IEnumerable<Customer> customers)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine(Header);
        foreach (var c in customers.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            writer.WriteLine(string.Join(";", Escape(c.Id), Escape(c.Name), Escape(c.Email), Escape(c.Subject),
                Escape(c.Body), Escape(c.SourceDirectory), "", c.Active ? "1" : "0"));
    }

    public static CsvImportResult Import(string path, IReadOnlyList<Customer> existing, ISecretProtector secrets)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var delimiter = DetectDelimiter(text);
        var rows = Parse(text, delimiter);
        if (rows.Count == 0) throw new UserException("Plik CSV jest pusty.");
        var header = rows[0].Select(x => x.Trim().ToLowerInvariant()).ToList();
        string[] required = ["nazwa", "email", "temat", "tresc", "katalog", "haslo_zip", "aktywny"];
        foreach (var name in required)
            if (!header.Contains(name)) throw new UserException($"CSV: brak kolumny '{name}'.");
        var byId = existing.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var byEmail = existing.Where(x => !string.IsNullOrWhiteSpace(x.Email))
            .GroupBy(x => x.Email.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byName = existing.GroupBy(x => x.Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.CurrentCultureIgnoreCase);
        var result = new List<Customer>();
        int added = 0, updated = 0;
        for (int rowNo = 1; rowNo < rows.Count; rowNo++)
        {
            var row = rows[rowNo];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            string Get(string name)
            {
                var index = header.IndexOf(name);
                return index >= 0 && index < row.Count ? row[index].Trim() : "";
            }
            var id = Get("id");
            var name = Get("nazwa");
            var email = Get("email");
            Customer current;
            if (id.Length > 0 && byId.TryGetValue(id, out var foundById))
            {
                current = foundById;
                updated++;
            }
            else if (email.Length > 0 && byEmail.TryGetValue(email, out var foundByEmail))
            {
                current = foundByEmail;
                updated++;
            }
            else if (byName.TryGetValue(name, out var foundByName))
            {
                current = foundByName;
                updated++;
            }
            else
            {
                current = new Customer();
                added++;
            }
            var password = Get("haslo_zip");
            var protectedPassword = password.Length == 0 ? current.ZipPasswordProtected : secrets.Protect(password);
            var requestedActive = ParseBool(Get("aktywny"));
            var customer = current with
            {
                Name = name,
                Email = email,
                Subject = Get("temat"),
                Body = Get("tresc"),
                SourceDirectory = Get("katalog"),
                Active = requestedActive && email.Length > 0 && protectedPassword.Length > 0,
                ZipPasswordProtected = protectedPassword
            };
            try { Validation.Customer(customer); }
            catch (UserException ex) { throw new UserException($"CSV wiersz {rowNo + 1}: {ex.Message}"); }
            result.Add(customer);
            byId[customer.Id] = customer;
            if (!string.IsNullOrWhiteSpace(customer.Email)) byEmail[customer.Email.Trim()] = customer;
            byName[customer.Name.Trim()] = customer;
        }
        return new CsvImportResult(added, updated, result);
    }

    private static bool ParseBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "tak" or "yes" => true,
        "" or "0" or "false" or "nie" or "no" => false,
        _ => throw new UserException($"CSV: niepoprawna wartość aktywny '{value}'. Użyj 1/0, tak/nie albo true/false.")
    };

    private static string Escape(string value)
    {
        if (!value.Contains(';') && !value.Contains('"') && !value.Contains('\r') && !value.Contains('\n')) return value;
        var quote = ((char)34).ToString();
        return quote + value.Replace(quote, quote + quote) + quote;
    }

    private static char DetectDelimiter(string text)
    {
        bool quoted = false;
        int semicolons = 0, commas = 0;
        foreach (var ch in text)
        {
            if (ch == '"') quoted = !quoted;
            else if (!quoted && ch == ';') semicolons++;
            else if (!quoted && ch == ',') commas++;
            else if (!quoted && (ch == '\r' || ch == '\n')) break;
        }
        if (semicolons == 0 && commas == 0)
            throw new UserException("CSV: nie można rozpoznać separatora kolumn. Użyj średnika lub przecinka.");
        return semicolons >= commas ? ';' : ',';
    }

    private static List<List<string>> Parse(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
            }
            else
            {
                if (ch == '"') quoted = true;
                else if (ch == delimiter) { row.Add(field.ToString()); field.Clear(); }
                else if (ch == '\r') { }
                else if (ch == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = new List<string>(); }
                else field.Append(ch);
            }
        }
        if (quoted) throw new UserException("CSV: niezamknięty cudzysłów.");
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
