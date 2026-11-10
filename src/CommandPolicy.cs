namespace FakeCheats;

// This policy applies only to commands dispatched on the SERVER.
// Client-local commands never pass through this code.
public sealed class CommandPolicy
{
    // A vanilla-game catalog distinguishes official names from third-party
    // commands without relying on plugin prefixes, registration order or ABI.
    private static readonly HashSet<string> OfficialCommands = LoadOfficialCommands();

    public static readonly string[] VisualCommands =
    [
        "mat_fullbright", "r_csgo_postprocess_enable", "firstperson",
        "thirdperson", "incrementvar", "invprev"
    ];

    // Usercmds (movement, firing, etc.) do not use the console dispatch hook.
    public static readonly string[] GameplayCommands =
    [
        "say", "say_team", "joingame", "jointeam", "joinclass", "teammenu", "status", "ping",
        "buy", "autobuy", "rebuy", "drop", "kill", "explode",
        "spec_next", "spec_prev", "spec_mode", "spec_player", "spec_pos",
        "spec_menu", "spec_autodirector", "player_ping", "voicemenu",
        "coverme", "takepoint", "holdpos", "regroup", "followme", "takingfire",
        "go", "fallback", "sticktog", "getinpos", "stormfront", "report",
        "roger", "enemyspot", "needbackup", "sectorclear", "inposition",
        "reportingin", "getout", "negative", "enemydown", "compliment",
        "thanks", "cheer", "sorry", "needrop", "playerradio",
        "callvote", "vote", "voted", "listissues", "show_loadout_toggle",
        "css_fc_status", "css_fc_sync"
    ];

    private readonly HashSet<string> _allowed = new(
        VisualCommands.Concat(GameplayCommands), StringComparer.OrdinalIgnoreCase);

    public CommandPolicy(IEnumerable<string> additionalCommands)
    {
        foreach (var name in additionalCommands)
        {
            if (!IsCommandName(name))
                throw new ArgumentException($"Invalid additional command name: {name}");
            _allowed.Add(name);
        }
    }

    public bool IsAllowed(bool isRoot, IReadOnlyList<string> args)
    {
        if (isRoot) return true;
        if (args.Count == 0 || !IsSingleCommand(args[0]) || args[0].Any(char.IsWhiteSpace)) return false;
        // Native noclip's FCVAR_CHEAT is removed while our permission hook is
        // installed. Never let configuration opt regular players into it.
        if (args[0].Equals("noclip", StringComparison.OrdinalIgnoreCase)) return false;

        // These variables must never be set globally through a server command.
        // The original local commands are enabled by sv_cheats spoofing instead.
        if (args[0].Equals("mat_fullbright", StringComparison.OrdinalIgnoreCase)
            || args[0].Equals("r_csgo_postprocess_enable", StringComparison.OrdinalIgnoreCase)
            || args[0].Equals("incrementvar", StringComparison.OrdinalIgnoreCase))
            return false;

        // Unknown names belong to the engine or other plugins to handle. In
        // particular, plugin callbacks must run their own permission checks.
        return !OfficialCommands.Contains(args[0]) || _allowed.Contains(args[0]);
    }

    private static HashSet<string> LoadOfficialCommands()
    {
        using var stream = typeof(CommandPolicy).Assembly.GetManifestResourceStream("FakeCheats.official-commands.txt")
            ?? throw new InvalidOperationException("The embedded official CS2 command catalog is missing.");
        using var reader = new StreamReader(stream);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            var name = line.Trim();
            if (name.Length == 0 || name.StartsWith('#')) continue;
            if (!IsCommandName(name))
                throw new InvalidOperationException($"Invalid official CS2 command name: {name}");
            names.Add(name);
        }
        return names;
    }

    public static bool IsCommandName(string? value) => !string.IsNullOrEmpty(value)
        && value.Length <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '+' or '-');

    public static bool IsSingleCommand(string value) => value.Length is > 0 and <= 1024
        && !value.Any(c => char.IsControl(c) || c == ';');
}
