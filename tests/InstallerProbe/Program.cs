using System.IO;

File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "launches.txt"),
    Environment.ProcessPath + Environment.NewLine);
