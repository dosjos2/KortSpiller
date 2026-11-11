namespace KortSpiller.Core;

public static class PlayerActions
{
    public static readonly string[] All = ["pause", "resume", "toggle", "next", "previous"];

    public static string Label(string action) => action switch
    {
        "pause" => "Pause",
        "resume" => "Fortsett",
        "toggle" => "Pause / fortsett",
        "next" => "Neste sang",
        "previous" => "Forrige sang",
        _ => action,
    };
}
