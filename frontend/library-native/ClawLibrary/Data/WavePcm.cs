using System.Buffers.Binary;

namespace ClawLibrary.Data;

public static class WavePcm
{
    public static byte[] WithVolume(byte[] original, int volume)
    {
        if (original.Length < 44 || !original.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !original.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("无效的 WAV 文件");
        var copy = (byte[])original.Clone();
        bool pcm16 = false, found = false;
        for (int offset = 12; offset + 8 <= copy.Length;)
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(offset + 4, 4));
            if (length > int.MaxValue || length > copy.Length - offset - 8) throw new InvalidDataException("WAV 数据不完整");
            int start = offset + 8, size = (int)length;
            if (copy.AsSpan(offset, 4).SequenceEqual("fmt "u8) && size >= 16)
                pcm16 = BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(start, 2)) == 1
                    && BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(start + 14, 2)) == 16;
            if (copy.AsSpan(offset, 4).SequenceEqual("data"u8))
            {
                if (!pcm16 || size % 2 != 0) throw new InvalidDataException("音效需要 16 位 PCM");
                double gain = Math.Clamp(volume, 0, 100) / 100.0;
                for (int i = start; i < start + size; i += 2)
                {
                    short sample = BinaryPrimitives.ReadInt16LittleEndian(copy.AsSpan(i, 2));
                    BinaryPrimitives.WriteInt16LittleEndian(copy.AsSpan(i, 2), (short)Math.Round(sample * gain));
                }
                found = true;
            }
            offset = start + size + (size & 1);
        }
        if (!found) throw new InvalidDataException("WAV 没有音频数据");
        return copy;
    }
}
