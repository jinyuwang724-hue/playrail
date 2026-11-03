using System.IO;
using System.Text;

namespace ClawSleepAnimation;

internal sealed record ButtonSignal(int EventId, string Device, uint Mask, DateTime EventTime, double DeliveryMs)
{
    public static ButtonSignal Parse(int id, byte[] bytes, DateTime eventTime, DateTime deliveryTime)
    {
        if (id != 24 && id != 25) throw new InvalidDataException("Unexpected ACPI event ID");
        int tail = id == 24 ? 8 : 12;
        if (bytes.Length < tail + 2 || (bytes.Length - tail - 2) % 2 != 0)
            throw new InvalidDataException("Unexpected ACPI button event size");
        int chars = BitConverter.ToUInt16(bytes, 0);
        if (chars * 2 != bytes.Length - tail - 2)
            throw new InvalidDataException("Unexpected ACPI button instance length");
        string device = Encoding.Unicode.GetString(bytes, 2, chars * 2).TrimEnd('\0');
        uint mask = BitConverter.ToUInt32(bytes, bytes.Length - (id == 24 ? 4 : 12));
        return new(id, device, mask, eventTime, (deliveryTime - eventTime).TotalMilliseconds);
    }
}
