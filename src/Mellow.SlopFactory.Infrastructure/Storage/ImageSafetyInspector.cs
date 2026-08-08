using Mellow.SlopFactory.Domain;
using System.Buffers.Binary;

namespace Mellow.SlopFactory.Infrastructure.Storage;

internal static class ImageSafetyInspector
{
    private const int MaximumDimension = 16_384;
    private const long MaximumPixels = 100_000_000;
    private const int MaximumAnimationFrames = 256;
    private const long MaximumDecodedAnimationBytes = 536_870_912;

    public static void Validate(ReadOnlySpan<byte> bytes, string mediaType)
    {
        if (mediaType == "image/svg+xml") return;
        var (width, height, frames) = ReadProperties(bytes, mediaType);
        if (width < 1 || height < 1) throw new LibraryValidationException("The image dimensions could not be validated safely.");
        if (width > MaximumDimension || height > MaximumDimension || (long)width * height > MaximumPixels)
        {
            throw new LibraryValidationException("Preview Too Complex or Large: the image dimensions exceed the safe viewer limit.");
        }
        if (frames > MaximumAnimationFrames || (long)width * height * 4 * frames > MaximumDecodedAnimationBytes)
        {
            throw new LibraryValidationException("Preview Too Complex or Large: the animation exceeds the safe viewer limit.");
        }
    }

    public static (int Width, int Height) ReadDimensions(ReadOnlySpan<byte> bytes, string mediaType)
    {
        if (mediaType == "image/svg+xml") return default;
        var (width, height, _) = ReadProperties(bytes, mediaType);
        if (width < 1 || height < 1) throw new LibraryValidationException("The image dimensions could not be read from the bounded technical metadata probe.");
        return (width, height);
    }

    public static int? ReadOrientation(ReadOnlySpan<byte> bytes, string mediaType)
    {
        return mediaType == "image/jpeg" ? ReadJpegOrientation(bytes) : null;
    }

    private static (int Width, int Height, int Frames) ReadProperties(ReadOnlySpan<byte> bytes, string mediaType) => mediaType switch
        {
            "image/png" => ReadPng(bytes),
            "image/jpeg" => ReadJpeg(bytes),
            "image/gif" => ReadGif(bytes),
            "image/webp" => ReadWebP(bytes),
            _ => throw new LibraryValidationException("This image format is not supported by the built-in viewer.")
        };

    private static (int Width, int Height, int Frames) ReadPng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24 || !bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) || !bytes[12..16].SequenceEqual("IHDR"u8)) return default;
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]);
        return (width > int.MaxValue ? int.MaxValue : (int)width, height > int.MaxValue ? int.MaxValue : (int)height, 1);
    }

    private static (int Width, int Height, int Frames) ReadGif(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 10 || (!bytes.StartsWith("GIF87a"u8) && !bytes.StartsWith("GIF89a"u8))) return default;
        var frames = 0;
        for (var index = 10; index < bytes.Length; index++) if (bytes[index] == 0x2C) frames++;
        return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]), Math.Max(frames, 1));
    }

    private static (int Width, int Height, int Frames) ReadJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return default;
        var index = 2;
        while (index + 4 <= bytes.Length)
        {
            while (index < bytes.Length && bytes[index] == 0xFF) index++;
            if (index >= bytes.Length) break;
            var marker = bytes[index++];
            if (marker is 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7) continue;
            if (index + 2 > bytes.Length) break;
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[index..(index + 2)]);
            if (segmentLength < 2 || index + segmentLength > bytes.Length) break;
            if (IsStartOfFrame(marker) && segmentLength >= 7)
            {
                return (BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 5)..(index + 7)]), BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 3)..(index + 5)]), 1);
            }
            index += segmentLength;
        }
        return default;
    }

    private static int? ReadJpegOrientation(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return null;
        var index = 2;
        while (index + 4 <= bytes.Length)
        {
            while (index < bytes.Length && bytes[index] == 0xFF) index++;
            if (index >= bytes.Length) return null;
            var marker = bytes[index++];
            if (marker is 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7) continue;
            if (index + 2 > bytes.Length) return null;
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[index..(index + 2)]);
            if (segmentLength < 2 || index + segmentLength > bytes.Length) return null;
            if (marker == 0xE1 && segmentLength >= 10)
            {
                var payload = bytes.Slice(index + 2, segmentLength - 2);
                if (payload[..6].SequenceEqual("Exif\0\0"u8)) return ReadExifOrientation(payload[6..]);
            }
            index += segmentLength;
        }
        return null;
    }

    private static int? ReadExifOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 14) return null;
        var littleEndian = tiff[..2].SequenceEqual("II"u8);
        if (!littleEndian && !tiff[..2].SequenceEqual("MM"u8)) return null;
        if (ReadUInt16(tiff[2..4], littleEndian) != 42) return null;
        var ifdOffset = ReadUInt32(tiff[4..8], littleEndian);
        if (ifdOffset > int.MaxValue || (long)ifdOffset + 2 > tiff.Length) return null;
        var offset = (int)ifdOffset;
        var entryCount = ReadUInt16(tiff[offset..(offset + 2)], littleEndian);
        if ((long)offset + 2 + entryCount * 12L > tiff.Length) return null;
        for (var entry = 0; entry < entryCount; entry++)
        {
            var entryOffset = offset + 2 + entry * 12;
            if (ReadUInt16(tiff[entryOffset..(entryOffset + 2)], littleEndian) != 0x0112 ||
                ReadUInt16(tiff[(entryOffset + 2)..(entryOffset + 4)], littleEndian) != 3 ||
                ReadUInt32(tiff[(entryOffset + 4)..(entryOffset + 8)], littleEndian) != 1)
            {
                continue;
            }
            var orientation = ReadUInt16(tiff[(entryOffset + 8)..(entryOffset + 10)], littleEndian);
            return orientation is >= 1 and <= 8 ? orientation : null;
        }
        return null;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static bool IsStartOfFrame(byte marker) => marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF;

    private static (int Width, int Height, int Frames) ReadWebP(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 30 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes[8..12].SequenceEqual("WEBP"u8)) return default;
        if (bytes[12..16].SequenceEqual("VP8X"u8)) return (1 + ReadUInt24(bytes[24..27]), 1 + ReadUInt24(bytes[27..30]), 1);
        if (bytes[12..16].SequenceEqual("VP8L"u8) && bytes.Length >= 25 && bytes[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..25]);
            return (1 + (int)(bits & 0x3FFF), 1 + (int)((bits >> 14) & 0x3FFF), 1);
        }
        if (bytes[12..16].SequenceEqual("VP8 "u8) && bytes.Length >= 30 && bytes[23..26].SequenceEqual(new byte[] { 0x9D, 0x01, 0x2A }))
        {
            return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..28]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..30]) & 0x3FFF, 1);
        }
        return default;
    }

    private static int ReadUInt24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;
}
