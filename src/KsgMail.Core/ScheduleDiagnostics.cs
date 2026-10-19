namespace KsgMail.Core;

public static class ScheduleDiagnostics
{
    private static readonly object gate = new();

    public static void Write(string dataDirectory, string message)
    {
        try
        {
            lock (gate)
            {
                var directory = Path.Combine(dataDirectory, "Logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"schedule-{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTimeOffset.Now:O} [{Environment.ProcessId}] {message.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
