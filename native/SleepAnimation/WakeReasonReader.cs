using System.Diagnostics.Eventing.Reader;

namespace ClawSleepAnimation;

internal static class WakeReasonReader
{
    public static async Task<int?> ReadAsync(DateTime wakeAt, CancellationToken token)
    {
        DateTime since = wakeAt.AddSeconds(-10), deadline = DateTime.UtcNow.AddSeconds(3);
        string query = "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=507 and TimeCreated[@SystemTime>='" +
            since.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture) + "']]]";
        while (DateTime.UtcNow <= deadline)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query) { ReverseDirection = true });
                using var item = reader.ReadEvent();
                if (item != null && item.Properties.Count > 9 && item.TimeCreated?.ToUniversalTime() >= since)
                    return Convert.ToInt32(item.Properties[9].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (EventLogException ex) { Program.Log("enhanced-wake-probe", ex.Message); return null; }
            await Task.Delay(250, token).ConfigureAwait(false);
        }
        return null;
    }
}
