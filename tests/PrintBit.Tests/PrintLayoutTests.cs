using PrintBit.Infrastructure.Services.DocumentProcessing;
using PrintBit.Infrastructure.Services.PrintService;

namespace PrintBit.Tests;

public class PrintLayoutTests
{
    [Theory]
    [InlineData("portrait", 0, 612, 792)]
    [InlineData("landscape", 90, 792, 612)]
    public void FitRotatedLetter_UsesOneAspectPreservingInset(string orientation, int rotation, double width, double height)
    {
        var layout = PrintLayout.Calculate(612, 792, new PrintJobSettings
        { PaperSize = "Letter", Orientation = orientation }, rotation);
        Assert.Equal(width, layout.Width);
        Assert.Equal(height, layout.Height);
        Assert.Equal(0.9529411765, layout.Scale, 8);
    }

    [Fact]
    public void ActualSize_LegalOnLetterDoesNotShrink()
    {
        var layout = PrintLayout.Calculate(612, 1008, new PrintJobSettings
        { PaperSize = "Letter", Scaling = "actual" }, 0);
        Assert.Equal(1, layout.Scale);
    }
}
