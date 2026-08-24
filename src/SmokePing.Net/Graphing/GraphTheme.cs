namespace SmokePing.Net.Graphing;

/// <summary>Colours used when rendering a graph.</summary>
public sealed record GraphTheme(
    string Background,
    string PlotBackground,
    string Grid,
    string Axis,
    string Text,
    string MutedText,
    string NoData,
    string SmokeBase)
{
    /// <summary>The classic SmokePing look: dark ink on a light canvas.</summary>
    public static readonly GraphTheme Light = new(
        Background: "#ffffff",
        PlotBackground: "#fbfbf7",
        Grid: "#d8d8d0",
        Axis: "#4a4a4a",
        Text: "#1c1c1c",
        MutedText: "#5c5c5c",
        NoData: "#eeeee4",
        SmokeBase: "#000000");

    public static readonly GraphTheme Dark = new(
        Background: "#161a20",
        PlotBackground: "#1c2129",
        Grid: "#2c333d",
        Axis: "#8a94a3",
        Text: "#e6e9ee",
        MutedText: "#98a2b3",
        NoData: "#232833",
        SmokeBase: "#ffffff");

    public static GraphTheme FromName(string? name) =>
        string.Equals(name, "dark", StringComparison.OrdinalIgnoreCase) ? Dark : Light;
}
