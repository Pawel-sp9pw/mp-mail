using System.Diagnostics;
using System.Security.Principal;
using System.Xml.Linq;

namespace KsgMail.Core;

public sealed class WindowsScheduler
{
    public string TaskName => "MP-Mail-" + WindowsIdentity.GetCurrent().User!.Value;

    public static XDocument BuildTaskXml(AppSettings settings, string executable, string userSid)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        return new XDocument(new XDeclaration("1.0", "utf-16", null),
            new XElement(ns + "Task", new XAttribute("version", "1.2"),
                new XElement(ns + "Triggers",
                    settings.GetScheduleTimes().Select(time => new XElement(ns + "CalendarTrigger",
                        new XElement(ns + "StartBoundary", "2020-01-01T" + time.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)),
                        new XElement(ns + "Enabled", true),
                        new XElement(ns + "ScheduleByDay", new XElement(ns + "DaysInterval", 1)))),
                    new XElement(ns + "CalendarTrigger",
                        new XElement(ns + "StartBoundary", "2020-01-01T00:00:00"),
                        new XElement(ns + "Enabled", true),
                        new XElement(ns + "Repetition",
                            new XElement(ns + "Interval", $"PT{settings.NegativeIntervalMinutes}M"),
                            new XElement(ns + "Duration", "P1D"),
                            new XElement(ns + "StopAtDurationEnd", false)),
                        new XElement(ns + "ScheduleByDay", new XElement(ns + "DaysInterval", 1))),
                    new XElement(ns + "LogonTrigger",
                        new XElement(ns + "Enabled", true),
                        new XElement(ns + "UserId", userSid))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(ns + "UserId", userSid),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "LeastPrivilege"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", false),
                    new XElement(ns + "StopIfGoingOnBatteries", false),
                    new XElement(ns + "StartWhenAvailable", true),
                    new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(ns + "Enabled", true)),
                new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", executable),
                        new XElement(ns + "Arguments", "--scheduled"),
                        new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(executable))))));
    }

    public async Task ApplyAsync(AppSettings settings, string executable)
    {
        if (!settings.ScheduleEnabled)
        {
            await RunAsync(["/Delete", "/TN", TaskName, "/F"], false);
            return;
        }
        if (!File.Exists(executable)) throw new UserException("Nie znaleziono aplikacji do harmonogramu. Zainstaluj wersję release.");
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var document = BuildTaskXml(settings, executable, sid);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            document.Save(path);
            await RunAsync(["/Create", "/TN", TaskName, "/XML", path, "/F"], true);
        }
        finally { File.Delete(path); }
    }

    private static async Task RunAsync(IEnumerable<string> arguments, bool requireSuccess)
    {
        var start = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        await error;
        if (requireSuccess && process.ExitCode != 0)
            throw new UserException("Nie udało się zapisać zadania Windows. Sprawdź uprawnienia Harmonogramu zadań.");
    }
}
