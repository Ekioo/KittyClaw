namespace KittyClaw.Core.Models;

public sealed record ProjectAvatarColors(string Background, string Cat, string Dots)
{
    private static readonly ProjectAvatarColors[] Dark =
    [
        new("#232039", "#B39AFF", "#38275F"), new("#34252E", "#FF9A8A", "#6E343B"),
        new("#17322F", "#75E2BD", "#20554A"), new("#1B2C43", "#82C3FF", "#274C77"),
        new("#352C28", "#FFC08A", "#69422D"), new("#322336", "#F6A0D8", "#65375E"),
        new("#2C3020", "#D4E982", "#4E5C24"), new("#1A3037", "#80DDE9", "#285D6B")
    ];
    private static readonly ProjectAvatarColors[] Colorful =
    [
        new("#6341C5", "#F1E9FF", "#6749B3"), new("#2261B8", "#E3F1FF", "#265E9C"),
        new("#A93266", "#FFE3EE", "#9E3A60"), new("#18795E", "#D7F9E9", "#1D705B"),
        new("#BC573A", "#FFE8CC", "#A34932"), new("#167B8B", "#DDF8F2", "#236D7B"),
        new("#853D9D", "#FBE0FF", "#7D3B8F"), new("#A66B15", "#FFF2CA", "#825518")
    ];
    private static readonly ProjectAvatarColors[] Light =
    [
        new("#EBE3FF", "#7950C8", "#F1E6FF"), new("#DCEEFF", "#2D79BA", "#E4F4FF"),
        new("#FFE0ED", "#B93F77", "#FFE8F2"), new("#D8F4E8", "#238365", "#E5FFF1"),
        new("#FFE5D4", "#B95B32", "#FFF1DE"), new("#D4F3F5", "#247E8E", "#E1FCF5"),
        new("#F2DFFF", "#8C4BB2", "#F9ECFF"), new("#FFEDC4", "#A46E20", "#FFF4D7")
    ];

    public static string NormalizeTheme(string? theme) => theme is "A" or "C" ? theme : "B";

    public static ProjectAvatarColors Select(byte paletteSeed, string? theme)
    {
        var palettes = NormalizeTheme(theme) switch { "A" => Dark, "C" => Light, _ => Colorful };
        return palettes[paletteSeed % palettes.Length];
    }
}
