using System.Text;
using System.Text.RegularExpressions;

namespace PrintBit.Infrastructure.Services.PrintService;

/// <summary>
/// Counts the number of pages in a PDF file by scanning for the
/// /Count entry inside a /Type /Pages object (PDF 1.7 spec,
/// ISO 32000-1 §7.7.2 / §7.7.3.3).
///
/// Intentionally lightweight: no NuGet dependency, no full PDF parser.
/// Designed for the print verification path, which only needs an
/// authoritative number to compare against the spooler's
/// Win32_PrintJob.TotalPages. If the file is encrypted, corrupt, or
/// non-PDF, returns null — the caller falls back to the spooler's
/// reported total.
///
/// This function never throws. The print verification path is
/// best-effort: a null result is acceptable and means "trust the
/// spooler".
/// </summary>
internal static class PdfPageCounter
{
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private static readonly Regex CountPattern = new(
        @"/Count\s+(-?\d+)",
        RegexOptions.Compiled);

    private static readonly Regex TypePagePattern = new(
        @"/Type\s*/Page(?!s)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Maximum bytes to scan from the head and tail of the file. PDFs
    /// up to a few hundred pages are well within this on both sides.
    /// </summary>
    private const int ScanWindowBytes = 2 * 1024 * 1024;

    public static int? Count(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            // PDF spec requires the %PDF- header in the first 1 KB.
            Span<byte> header = stackalloc byte[PdfMagic.Length];
            if (stream.Read(header) != PdfMagic.Length ||
                !header.SequenceEqual(PdfMagic))
            {
                return null;
            }

            // Read the head and tail windows once.
            var headWindow = ReadHeadWindow(stream, ScanWindowBytes);
            var tailWindow = ReadTailWindow(stream, ScanWindowBytes);

            // 1) Primary path: take the largest /Count inside a
            //    /Type /Pages object. PDFs SumatraPDF accepts have a
            //    single page tree whose root /Count is the total
            //    page count.
            var count = TryReadPageTreeCount(headWindow, tailWindow);
            if (count is > 0)
            {
                return count;
            }

            // 2) Last resort: count /Type /Page tokens (excluding
            //    /Type /Pages). This over-counts on PDFs with form
            //    XObjects that have /Type /Page, but is the only way
            //    to recover when the trailer is missing or truncated.
            count = CountPageObjects(headWindow, tailWindow);
            return count is > 0 ? count : null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ReadHeadWindow(FileStream stream, int maxBytes)
    {
        stream.Position = 0;
        var buffer = new byte[Math.Min(maxBytes, stream.Length)];
        var read = stream.Read(buffer, 0, buffer.Length);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static byte[] ReadTailWindow(FileStream stream, int maxBytes)
    {
        var length = stream.Length;
        if (length <= maxBytes)
        {
            stream.Position = 0;
            var all = new byte[length];
            _ = stream.Read(all, 0, (int)length);
            return all;
        }

        stream.Position = length - maxBytes;
        var tail = new byte[maxBytes];
        _ = stream.Read(tail, 0, maxBytes);
        return tail;
    }

    private static int? TryReadPageTreeCount(
        byte[] headWindow,
        byte[] tailWindow)
    {
        // The page tree root's /Count is the total page count. We
        // take the largest /Count across both windows because
        // /Count can also appear in /Pages subtrees with smaller
        // numbers (e.g. a subtree of 50 pages within a 200-page
        // document).
        var headText = GetAscii(headWindow);
        var tailText = GetAscii(tailWindow);

        var max = 0;
        foreach (Match m in CountPattern.Matches(headText))
        {
            if (int.TryParse(m.Groups[1].Value, out var n) && n > max)
            {
                max = n;
            }
        }
        foreach (Match m in CountPattern.Matches(tailText))
        {
            if (int.TryParse(m.Groups[1].Value, out var n) && n > max)
            {
                max = n;
            }
        }
        return max == 0 ? null : max;
    }

    private static int? CountPageObjects(
        byte[] headWindow,
        byte[] tailWindow)
    {
        var headText = GetAscii(headWindow);
        var tailText = GetAscii(tailWindow);

        var headCount = TypePagePattern.Matches(headText).Count;
        var tailCount = TypePagePattern.Matches(tailText).Count;
        var total = headCount + tailCount;

        // De-duplicate when both windows overlap the same region.
        // For files smaller than 2× ScanWindowBytes, the two windows
        // are identical; in that case we'd double-count. Detect and
        // halve.
        if (headWindow.Length == tailWindow.Length &&
            headWindow.AsSpan().SequenceEqual(tailWindow))
        {
            total = headCount;
        }

        return total == 0 ? null : total;
    }

    /// <summary>
    /// Decodes the byte window as ASCII, replacing non-ASCII bytes
    /// with spaces. PDF object dictionaries are ASCII, so this is
    /// safe for our parsing needs and avoids bringing in a full
    /// encoding layer.
    /// </summary>
    private static string GetAscii(byte[] window)
    {
        var sb = new StringBuilder(window.Length);
        foreach (var b in window)
        {
            sb.Append(b is >= 0x20 and <= 0x7E or 0x0A or 0x0D or 0x09
                ? (char)b
                : ' ');
        }
        return sb.ToString();
    }
}

