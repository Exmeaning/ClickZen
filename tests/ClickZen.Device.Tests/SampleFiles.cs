namespace ClickZen.Device.Tests;

internal static class SampleFiles
{
    public static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", relativePath));
}
