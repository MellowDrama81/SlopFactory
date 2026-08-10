using System.Buffers.Binary;
using System.Text;
using Mellow.SlopFactory.Domain;

namespace Mellow.SlopFactory.Infrastructure.Storage;

internal static class MediaTechnicalInspector
{
    private const int MaximumProbeBytes = 4 * 1024 * 1024;

    public static async Task<MediaTechnicalProperties> InspectAsync(string path, string mediaType, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            var length = (int)Math.Min(info.Length, MaximumProbeBytes);
            var bytes = new byte[length];
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (mediaType == "audio/wav") return InspectWave(bytes, info.Length);
            if (mediaType is "audio/mp4" or "video/mp4") return InspectMp4(bytes);
            if (mediaType == "audio/mpeg") return InspectMpegAudio(bytes, info.Length);
            if (mediaType == "audio/flac") return InspectFlac(bytes);
            if (mediaType == "audio/ogg") return InspectOpus(bytes);
            if (mediaType == "audio/aac") return InspectAac(bytes);
            return new MediaTechnicalProperties(null, null, null, null, null, null, null, null, null, false, "Technical properties are unavailable for this media format.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentOutOfRangeException)
        {
            return new MediaTechnicalProperties(null, null, null, null, null, null, null, null, null, false, "Technical properties could not be read safely.");
        }
    }

    private static MediaTechnicalProperties InspectMpegAudio(ReadOnlySpan<byte> bytes, long fileLength)
    {
        var offset = bytes.StartsWith("ID3"u8) && bytes.Length >= 10
            ? 10 + ((bytes[6] & 0x7F) << 21) + ((bytes[7] & 0x7F) << 14) + ((bytes[8] & 0x7F) << 7) + (bytes[9] & 0x7F)
            : 0;
        while (offset + 4 <= bytes.Length && !(bytes[offset] == 0xFF && (bytes[offset + 1] & 0xE0) == 0xE0)) offset++;
        if (offset + 4 > bytes.Length) throw new InvalidDataException();
        var versionBits = (bytes[offset + 1] >> 3) & 0x03;
        var layerBits = (bytes[offset + 1] >> 1) & 0x03;
        var bitrateIndex = (bytes[offset + 2] >> 4) & 0x0F;
        var sampleIndex = (bytes[offset + 2] >> 2) & 0x03;
        if (layerBits != 1 || bitrateIndex is 0 or 15 || sampleIndex == 3) throw new InvalidDataException();
        int[] mpeg1Bitrates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
        int[] mpeg2Bitrates = [8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
        var bitrate = (versionBits == 3 ? mpeg1Bitrates : mpeg2Bitrates)[bitrateIndex - 1] * 1000;
        int[] baseRates = [44_100, 48_000, 32_000];
        var sampleRate = baseRates[sampleIndex] / (versionBits == 3 ? 1 : versionBits == 2 ? 2 : 4);
        var channelMode = (bytes[offset + 3] >> 6) & 0x03;
        var duration = TimeSpan.FromSeconds(fileLength * 8d / bitrate);
        return new MediaTechnicalProperties(duration, "MPEG Audio", "MP3", null, channelMode == 3 ? 1 : 2, sampleRate, null, null, null, true);
    }

    private static MediaTechnicalProperties InspectFlac(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 42 || !bytes[..4].SequenceEqual("fLaC"u8) || (bytes[4] & 0x7F) != 0) throw new InvalidDataException();
        var streamInfo = bytes.Slice(8, 34);
        var packed = BinaryPrimitives.ReadUInt64BigEndian(streamInfo.Slice(10, 8));
        var sampleRate = (int)((packed >> 44) & 0xFFFFF);
        var channels = (int)((packed >> 41) & 0x07) + 1;
        var totalSamples = packed & 0xFFFFFFFFF;
        TimeSpan? duration = sampleRate > 0 ? TimeSpan.FromSeconds((double)totalSamples / sampleRate) : null;
        return new MediaTechnicalProperties(duration, "FLAC", "FLAC", null, channels, sampleRate, null, null, null, true);
    }

    private static MediaTechnicalProperties InspectOpus(ReadOnlySpan<byte> bytes)
    {
        var index = bytes.IndexOf("OpusHead"u8);
        if (index < 0 || index + 19 > bytes.Length) throw new InvalidDataException();
        var channels = bytes[index + 9];
        var sourceRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(index + 12, 4));
        return new MediaTechnicalProperties(null, "Ogg", "Opus", null, channels, sourceRate == 0 ? 48_000 : checked((int)sourceRate), null, null, null, true);
    }

    private static MediaTechnicalProperties InspectAac(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 7 || bytes[0] != 0xFF || (bytes[1] & 0xF6) != 0xF0) throw new InvalidDataException();
        int[] sampleRates = [96_000, 88_200, 64_000, 48_000, 44_100, 32_000, 24_000, 22_050, 16_000, 12_000, 11_025, 8_000, 7_350];
        var sampleIndex = (bytes[2] >> 2) & 0x0F;
        if (sampleIndex >= sampleRates.Length) throw new InvalidDataException();
        var channels = ((bytes[2] & 1) << 2) | (bytes[3] >> 6);
        return new MediaTechnicalProperties(null, "ADTS", "AAC", null, channels, sampleRates[sampleIndex], null, null, null, true);
    }

    private static MediaTechnicalProperties InspectWave(ReadOnlySpan<byte> bytes, long fileLength)
    {
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8)) throw new InvalidDataException();
        ushort? format = null;
        ushort? channels = null;
        uint? sampleRate = null;
        ushort? bits = null;
        uint? byteRate = null;
        uint? dataBytes = null;
        for (var offset = 12; offset + 8 <= bytes.Length;)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            var available = Math.Min((long)size, bytes.Length - offset - 8L);
            if (bytes.Slice(offset, 4).SequenceEqual("fmt "u8) && available >= 16)
            {
                var payload = bytes[(offset + 8)..];
                format = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]);
            }
            else if (bytes.Slice(offset, 4).SequenceEqual("data"u8)) dataBytes = size;
            var next = offset + 8L + size + (size & 1);
            if (next > int.MaxValue || next <= offset) break;
            offset = (int)next;
        }
        if (format is null || channels is null || sampleRate is null) throw new InvalidDataException();
        var durationBytes = dataBytes ?? (uint)Math.Min(uint.MaxValue, Math.Max(0, fileLength - 44));
        TimeSpan? duration = byteRate > 0 ? TimeSpan.FromSeconds((double)durationBytes / byteRate.Value) : null;
        var codec = format switch { 1 => $"PCM {bits}-bit", 3 => $"IEEE float {bits}-bit", 6 => "A-law", 7 => "mu-law", _ => $"WAVE format {format}" };
        return new MediaTechnicalProperties(duration, "WAVE", codec, null, channels, checked((int)sampleRate.Value), null, null, null, true);
    }

    private static MediaTechnicalProperties InspectMp4(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes.Slice(4, 4).SequenceEqual("ftyp"u8)) throw new InvalidDataException();
        TimeSpan? duration = null;
        int? width = null;
        int? height = null;
        for (var index = 0; index + 12 < bytes.Length; index++)
        {
            if (bytes.Slice(index, 4).SequenceEqual("mvhd"u8))
            {
                var version = bytes[index + 4];
                var baseOffset = index + (version == 1 ? 24 : 16);
                if (baseOffset + (version == 1 ? 12 : 8) <= bytes.Length)
                {
                    var scale = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(baseOffset, 4));
                    var units = version == 1 ? BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(baseOffset + 4, 8)) : BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(baseOffset + 4, 4));
                    if (scale > 0) duration = TimeSpan.FromSeconds((double)units / scale);
                }
            }
            else if (bytes.Slice(index, 4).SequenceEqual("tkhd"u8))
            {
                var version = bytes[index + 4];
                var payloadLength = version == 1 ? 96 : 84;
                if (index + payloadLength <= bytes.Length)
                {
                    var w = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(index + payloadLength - 8, 4)) >> 16;
                    var h = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(index + payloadLength - 4, 4)) >> 16;
                    if (w > 0 && h > 0) { width = checked((int)w); height = checked((int)h); }
                }
            }
        }
        var ascii = Encoding.ASCII.GetString(bytes);
        var videoCodec = ascii.Contains("avc1", StringComparison.Ordinal) ? "H.264/AVC" : ascii.Contains("hvc1", StringComparison.Ordinal) || ascii.Contains("hev1", StringComparison.Ordinal) ? "H.265/HEVC" : ascii.Contains("vp09", StringComparison.Ordinal) ? "VP9" : null;
        var audioCodec = ascii.Contains("mp4a", StringComparison.Ordinal) ? "AAC/MPEG-4 Audio" : ascii.Contains("Opus", StringComparison.Ordinal) ? "Opus" : null;
        return new MediaTechnicalProperties(duration, "ISO Base Media", audioCodec, videoCodec, null, null, null, width, height, duration is not null || audioCodec is not null || videoCodec is not null);
    }
}
