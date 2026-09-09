using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PrintBit.Infrastructure.Services.PrintService;

namespace PrintBit.Infrastructure.Services.DocumentProcessing;

public sealed class DocumentPreprocessor : IDocumentPreprocessor
{
    private const double SafeMarginPoints = 14.4;

    public Task<PreparedDocument> PrepareAsync(
        string sourcePath,
        PrintJobSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Print source was not found", sourcePath);
        }

        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"printbit-prepared-{Guid.NewGuid():N}.pdf");
        try
        {
            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            var pageCount = extension == ".pdf"
                ? PreparePdf(sourcePath, outputPath, settings, cancellationToken)
                : PrepareImage(sourcePath, outputPath, settings);
            return Task.FromResult<PreparedDocument>(
                new PreparedDocument(outputPath, pageCount, [outputPath]));
        }
        catch
        {
            try { File.Delete(outputPath); } catch { }
            throw;
        }
    }

    private static int PreparePdf(
        string sourcePath,
        string outputPath,
        PrintJobSettings settings,
        CancellationToken cancellationToken)
    {
        using var form = XPdfForm.FromFile(sourcePath);
        using var output = new PdfDocument();
        var selectedPages = SelectPages(form.PageCount, settings.PageRange);
        foreach (var pageNumber in selectedPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            form.PageNumber = pageNumber;

            var page = output.AddPage();
            SetPaperGeometry(page, settings.PaperSize, settings.Orientation);

            var pageNativeRotation = form.Page?.Rotate ?? 0;
            var totalRotation = NormalizeRotation(pageNativeRotation + settings.RotationDeg);

            RenderFormToPage(XGraphics.FromPdfPage(page), form, page, totalRotation);
        }

        if (settings.Duplex && output.PageCount % 2 == 1)
        {
            var blank = output.AddPage();
            SetPaperGeometry(blank, settings.PaperSize, settings.Orientation);
        }

        var outputPageCount = output.PageCount;
        output.Save(outputPath);
        return outputPageCount;
    }

    private static void RenderFormToPage(XGraphics graphics, XPdfForm form, PdfPage page, int rotation)
    {
        using (graphics)
        {
            var rawW = form.PointWidth;
            var rawH = form.PointHeight;

            var is90or270 = rotation is 90 or 270;
            var contentW = is90or270 ? rawH : rawW;
            var contentH = is90or270 ? rawW : rawH;

            var availW = Math.Max(1.0, page.Width.Point - (2 * SafeMarginPoints));
            var availH = Math.Max(1.0, page.Height.Point - (2 * SafeMarginPoints));

            var scale = Math.Min(availW / contentW, availH / contentH);

            var destW = rawW * scale;
            var destH = rawH * scale;

            var cx = page.Width.Point / 2;
            var cy = page.Height.Point / 2;

            if (rotation != 0)
            {
                graphics.RotateAtTransform(rotation, new XPoint(cx, cy));
            }

            graphics.DrawImage(
                form,
                cx - (destW / 2),
                cy - (destH / 2),
                destW,
                destH);
        }
    }

    private static int PrepareImage(
        string sourcePath,
        string outputPath,
        PrintJobSettings settings)
    {
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"
        };
        if (!supported.Contains(Path.GetExtension(sourcePath)))
        {
            throw new InvalidDataException("Document preprocessing requires a PDF or supported image");
        }

        using var image = XImage.FromFile(sourcePath);
        using var output = new PdfDocument();
        var page = output.AddPage();
        SetPaperGeometry(page, settings.PaperSize, settings.Orientation);

        var rotation = NormalizeRotation(settings.RotationDeg);
        RenderImageToPage(XGraphics.FromPdfPage(page), image, page, rotation);

        output.Save(outputPath);
        return 1;
    }

    private static void RenderImageToPage(XGraphics graphics, XImage image, PdfPage page, int rotation)
    {
        using (graphics)
        {
            var rawW = (double)image.PixelWidth;
            var rawH = (double)image.PixelHeight;

            var is90or270 = rotation is 90 or 270;
            var contentW = is90or270 ? rawH : rawW;
            var contentH = is90or270 ? rawW : rawH;

            var availW = Math.Max(1.0, page.Width.Point - (2 * SafeMarginPoints));
            var availH = Math.Max(1.0, page.Height.Point - (2 * SafeMarginPoints));

            var scale = Math.Min(availW / contentW, availH / contentH);

            var destW = rawW * scale;
            var destH = rawH * scale;

            var cx = page.Width.Point / 2;
            var cy = page.Height.Point / 2;

            if (rotation != 0)
            {
                graphics.RotateAtTransform(rotation, new XPoint(cx, cy));
            }

            graphics.DrawImage(
                image,
                cx - (destW / 2),
                cy - (destH / 2),
                destW,
                destH);
        }
    }

    private static void SetPaperGeometry(PdfPage page, string? paperSize, string? orientation)
    {
        var (width, height) = paperSize?.ToUpperInvariant() switch
        {
            "LETTER" => (612d, 792d),
            "LEGAL" => (612d, 1008d),
            _ => (595.28d, 841.89d)
        };
        if (string.Equals(orientation, "landscape", StringComparison.OrdinalIgnoreCase))
        {
            (width, height) = (height, width);
        }
        page.Width = XUnit.FromPoint(width);
        page.Height = XUnit.FromPoint(height);
    }

    private static IReadOnlyList<int> SelectPages(int pageCount, string? pageRange)
    {
        if (string.IsNullOrWhiteSpace(pageRange))
        {
            return Enumerable.Range(1, pageCount).ToArray();
        }

        var selected = new SortedSet<int>();
        foreach (var chunk in pageRange.Split(',', StringSplitOptions.TrimEntries))
        {
            var endpoints = chunk.Split('-', StringSplitOptions.TrimEntries);
            if (!int.TryParse(endpoints[0], out var start) ||
                start < 1 || start > pageCount)
            {
                throw new InvalidDataException("Invalid page range");
            }
            var end = start;
            if (endpoints.Length == 2 &&
                (!int.TryParse(endpoints[1], out end) || end < start || end > pageCount))
            {
                throw new InvalidDataException("Invalid page range");
            }
            if (endpoints.Length > 2) throw new InvalidDataException("Invalid page range");
            for (var page = start; page <= end; page++) selected.Add(page);
        }
        if (selected.Count == 0) throw new InvalidDataException("Page range selected no pages");
        return selected.ToArray();
    }

    private static int NormalizeRotation(int value) => (((value % 360) + 360) % 360) switch
    {
        90 => 90,
        180 => 180,
        270 => 270,
        _ => 0
    };
}
