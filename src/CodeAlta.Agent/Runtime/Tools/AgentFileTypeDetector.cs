using System.Buffers;

namespace CodeAlta.Agent.Runtime.Tools;

internal static class AgentFileTypeDetector
{
    private const int BinaryProbeByteCount = 8192;

    public static bool IsProbablyBinaryFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var buffer = ArrayPool<byte>.Shared.Rent(BinaryProbeByteCount);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.SequentialScan);

            var totalRead = 0;
            while (totalRead < BinaryProbeByteCount)
            {
                var read = stream.Read(buffer, totalRead, BinaryProbeByteCount - totalRead);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            return buffer.AsSpan(0, totalRead).IndexOf((byte)0) >= 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
