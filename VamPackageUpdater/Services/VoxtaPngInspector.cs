using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using VamPackageUpdater.Models;

namespace VamPackageUpdater.Services;

/// <summary>
/// Peeks at the Voxta metadata embedded in an exported PNG. Voxta writes a
/// tEXt chunk with keyword "voxta_character" / "voxta_scenario" / "voxta_book" /
/// "voxta_package" whose text is a base64-encoded VOXPKG (zip). The UUID of the
/// contained resource appears as a plain-text substring in the decoded bytes
/// (via filenames in the zip's central directory), so we can verify which
/// resource a PNG belongs to before attaching it.
/// </summary>
public sealed class VoxtaPngInspector
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static readonly Regex UuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, VoxtaResourceKind> KindByKeyword = new(StringComparer.Ordinal)
    {
        ["voxta_character"] = VoxtaResourceKind.Character,
        ["voxta_scenario"]  = VoxtaResourceKind.Scenario,
        ["voxta_book"]      = VoxtaResourceKind.MemoryBook,
        ["voxta_package"]   = VoxtaResourceKind.Package
    };

    /// <summary>
    /// Result of inspecting a PNG for Voxta metadata.
    /// </summary>
    public sealed record InspectionResult(
        bool IsPng,
        bool HasVoxtaChunk,
        VoxtaResourceKind? EmbeddedKind,
        IReadOnlyList<string> EmbeddedUuids)
    {
        public static InspectionResult NotPng() => new(false, false, null, Array.Empty<string>());
        public static InspectionResult PngWithoutVoxtaChunk() => new(true, false, null, Array.Empty<string>());
    }

    public InspectionResult Inspect(string pngPath)
    {
        try
        {
            using var fs = File.OpenRead(pngPath);
            Span<byte> sig = stackalloc byte[8];
            if (fs.Read(sig) != 8 || !sig.SequenceEqual(PngSignature))
                return InspectionResult.NotPng();

            while (TryReadChunk(fs, out var type, out var data))
            {
                if (type == "tEXt")
                {
                    var (keyword, text) = DecodeTextChunk(data);
                    if (!KindByKeyword.TryGetValue(keyword, out var kind)) continue;

                    byte[] decoded;
                    try { decoded = Convert.FromBase64String(text); }
                    catch (FormatException) { return new InspectionResult(true, true, kind, Array.Empty<string>()); }

                    // Search decoded bytes for UUID substrings. Zip filenames in the
                    // central directory are uncompressed, so resource UUIDs in filenames
                    // like "{uuid}.character.json" appear as plain ASCII.
                    var ascii = Encoding.ASCII.GetString(decoded);
                    var uuids = UuidPattern.Matches(ascii)
                        .Select(m => m.Value.ToLowerInvariant())
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    return new InspectionResult(true, true, kind, uuids);
                }

                if (type == "IEND") break;
            }

            return InspectionResult.PngWithoutVoxtaChunk();
        }
        catch (IOException)
        {
            return InspectionResult.NotPng();
        }
    }

    /// <summary>
    /// Returns true if this PNG represents the given resource (kind match + UUID match).
    /// Out-parameter `reason` describes the mismatch when false.
    /// When the PNG has no Voxta chunk we return true with reason="no voxta metadata"
    /// so the caller can warn but not block — advanced users may be attaching
    /// non-Voxta PNGs on purpose.
    /// </summary>
    public enum MatchVerdict { Match, NoVoxtaMetadata, KindMismatch, UuidMismatch, NotAPng }

    public MatchVerdict CheckMatch(string pngPath, VoxtaResourceKind expectedKind, string expectedUuid, out InspectionResult result)
    {
        result = Inspect(pngPath);
        if (!result.IsPng) return MatchVerdict.NotAPng;
        if (!result.HasVoxtaChunk) return MatchVerdict.NoVoxtaMetadata;
        if (result.EmbeddedKind != expectedKind) return MatchVerdict.KindMismatch;

        var target = expectedUuid.ToLowerInvariant();
        return result.EmbeddedUuids.Contains(target, StringComparer.Ordinal)
            ? MatchVerdict.Match
            : MatchVerdict.UuidMismatch;
    }

    private static bool TryReadChunk(Stream stream, out string type, out byte[] data)
    {
        type = "";
        data = Array.Empty<byte>();
        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) != 8) return false;

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        type = Encoding.ASCII.GetString(header[4..]);

        if (length > int.MaxValue) return false;
        data = new byte[length];
        var total = 0;
        while (total < data.Length)
        {
            var read = stream.Read(data, total, data.Length - total);
            if (read == 0) return false;
            total += read;
        }

        // Skip CRC
        Span<byte> crc = stackalloc byte[4];
        if (stream.Read(crc) != 4) return false;
        return true;
    }

    private static (string Keyword, string Text) DecodeTextChunk(byte[] data)
    {
        // tEXt: keyword (Latin-1, 1-79 chars) + 0x00 + text (Latin-1)
        var nullIdx = Array.IndexOf(data, (byte)0);
        if (nullIdx < 0) return ("", "");
        var keyword = Encoding.Latin1.GetString(data, 0, nullIdx);
        var text = Encoding.Latin1.GetString(data, nullIdx + 1, data.Length - nullIdx - 1);
        return (keyword, text);
    }
}
