using PrintBit.Shared.Configurations;

namespace PrintBit.Infrastructure.Services.PrintService;

internal static class PrinterProfileResolver
{
    public static string Resolve(HardwareSettings settings, string? quality)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.Equals(quality, "standard", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(settings.PrinterProfiles.Standard)
                ? settings.PrinterName
                : settings.PrinterProfiles.Standard;
        }

        if (string.Equals(quality, "high", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(settings.PrinterProfiles.High))
            {
                throw new InvalidOperationException(
                    "High-quality printer profile is not configured.");
            }

            return settings.PrinterProfiles.High;
        }

        throw new ArgumentException(
            $"Unsupported print quality '{quality}'.",
            nameof(quality));
    }
}
