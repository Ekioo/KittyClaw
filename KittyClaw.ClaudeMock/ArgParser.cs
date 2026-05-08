namespace KittyClaw.ClaudeMock;

internal static class ArgParser
{
    public static string? Get(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
