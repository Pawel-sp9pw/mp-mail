using System.Diagnostics;
using System.Security.Principal;
using System.Threading;

namespace KsgMail.App;

public sealed class ScheduledRunSignal : IDisposable
{
    private readonly EventWaitHandle signal;
    private readonly RegisteredWaitHandle registration;

    public static string Name => @"Local\MP-Mail-Schedule-" + WindowsIdentity.GetCurrent().User!.Value;

    public ScheduledRunSignal(string name, Action onRequest)
    {
        signal = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => onRequest(), null, Timeout.Infinite, false);
    }

    public static async Task<bool> NotifyAsync(string name, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EventWaitHandle.TryOpenExisting(name, out var existing))
            {
                using (existing) return existing.Set();
            }
            await Task.Delay(100, cancellationToken);
        } while (elapsed.Elapsed < timeout);
        return false;
    }

    public void Dispose()
    {
        registration.Unregister(null);
        signal.Dispose();
    }
}
