namespace SpaceManager.Services;

public static class SizeFormatter
{
    private static readonly string[] Units = ["o", "Ko", "Mo", "Go", "To", "Po"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
            return "—";

        if (bytes == 0)
            return "0 o";

        var value = (double)bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < Units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes:N0} {Units[unitIndex]}"
            : $"{value:0.##} {Units[unitIndex]}";
    }
}
