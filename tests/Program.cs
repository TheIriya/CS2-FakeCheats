using FakeCheats;
using System.Text;
using System.Text.Json;

var policy = new CommandPolicy([]);
var checks = 0;
void Check(bool condition, string description)
{
    checks++;
    if (!condition) throw new Exception($"FAILED: {description}");
}

foreach (var name in new[]
{
    "noclip", "NoClip", "god", "buddha", "give", "ent_fire", "ent_create",
    "bot_add", "sv_cheats", "host_timescale", "exec", "toggle", "setvar",
    "thirdperson;noclip",
    "thirdperson\nnoclip", "cmd noclip", ""
})
{
    Check(!policy.IsAllowed(false, [name]), $"Non-root must not dispatch {name}");
    Check(policy.IsAllowed(true, [name]), $"Root bypasses plugin gate for {name}");
}

Check(!policy.IsAllowed(false, []), "Empty dispatch fails closed");
// Keep this sequence explicit: iterating GameplayCommands alone cannot catch a
// missing command in CS2's connection and team-selection flow.
foreach (var teamCommand in new string[][]
{
    ["joingame"], ["JOINGAME"], ["teammenu"],
    ["jointeam", "2"], ["jointeam", "3"], ["jointeam", "0"],
    ["jointeam", "1"], ["joinclass", "0"]
})
    Check(policy.IsAllowed(false, teamCommand), $"Non-root can enter the game and select a team: {string.Join(' ', teamCommand)}");
foreach (var name in CommandPolicy.GameplayCommands)
    Check(policy.IsAllowed(false, [name, "example argument"]), $"Normal command: {name}");
foreach (var name in new[] { "firstperson", "THIRDPERSON", "invprev" })
    Check(policy.IsAllowed(false, [name]), $"Allowed server dispatch: {name}");

// These run locally in normal use. Never mutate server-global variables through
// cmd/forwarding, even if the target is a supported visual variable.
Check(!policy.IsAllowed(false, ["mat_fullbright", "1"]), "No global fullbright mutation");
Check(!policy.IsAllowed(false, ["r_csgo_postprocess_enable", "0"]), "No global postprocessing mutation");
Check(!policy.IsAllowed(false, ["incrementvar", "mat_fullbright", "0", "1", "1"]), "No global visual increment");
Check(!policy.IsAllowed(false, ["incrementvar", "sv_cheats", "0", "1", "1"]), "No incrementvar cheat switch bypass");
Check(!policy.IsAllowed(false, ["incrementvar", "host_timescale", "0", "5", "1"]), "No incrementvar timescale bypass");

var extended = new CommandPolicy(["css_rtv"]);
Check(extended.IsAllowed(false, ["CSS_RTV"]), "Explicit plugin command opt-in is case-insensitive");
// Plugin commands must reach their own callbacks and permission checks, even
// without css_ prefixes, without configuration, or with cheat-like names.
foreach (var name in new[]
{
    "css_rtv", "CSS_NOMINATE", "css_noclip", "css_fc_noclip", "css_fc_exec",
    "fakecheats_enabled", "css_fc_enabled", "rtv", "menu", "sm_shop",
    "myplugin_give", "ent_custommenu", "sv_customvote", "unknown_future_command",
    "shop.open", "plugin:menu", "自定义菜单"
})
    Check(policy.IsAllowed(false, [name, "example argument"]), $"Plugin or unknown command passes through: {name}");
Check(!policy.IsAllowed(false, ["ent_remove", "!self"]), "Official command outside the old deny examples stays restricted");
Check(!policy.IsAllowed(false, ["mp_restartgame", "1"]), "Official server variable stays restricted");
var officialOptIn = new CommandPolicy(["mp_restartgame"]);
Check(officialOptIn.IsAllowed(false, ["MP_RESTARTGAME", "1"]), "Existing exact-name opt-ins still allow official commands");
var noclipOptIn = new CommandPolicy(["noclip"]);
Check(!noclipOptIn.IsAllowed(false, ["noclip"]), "Native noclip cannot be granted to non-root through configuration");
Check(!noclipOptIn.IsAllowed(false, ["NOCLIP"]), "Native noclip root requirement is case-insensitive");
Check(noclipOptIn.IsAllowed(true, ["noclip"]), "Root retains native noclip access");
foreach (var name in new[] { "*", "css_*", "css_rtv;noclip", "noclip\n", "say hi", "" })
{
    var rejected = false;
    try { _ = new CommandPolicy([name]); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, $"Malformed/wildcard configuration rejected: {name}");
}

Check(CommandPolicy.IsSingleCommand("noclip"), "Root can submit a single command");
Check(CommandPolicy.IsSingleCommand("ent_fire !self sethealth 100"), "Root can submit arguments");
foreach (var input in new[] { "", "noclip;god", "noclip\ngod", "noclip\rgod", "noclip\0", new string('a', 1025) })
    Check(!CommandPolicy.IsSingleCommand(input), "Root dispatcher rejects command chains and invalid payloads");

// Reproduce CS2 startup with no global variables: any premature player access
// throws, just like Utilities.GetPlayers/GetMaxClients in the reported stack.
var world = new WorldLifecycle();
var globalsInitialized = false;
var playerReads = 0;
var timerStarts = 0;
void EnumeratePlayers()
{
    if (!globalsInitialized) throw new InvalidOperationException("Global Variables not initialized yet.");
    playerReads++;
}
void EnumerateIfReady()
{
    if (world.IsReady) EnumeratePlayers();
}
void InitializeWorld()
{
    EnumerateIfReady();
    timerStarts++;
}

world.Load();
var initialUpdate = world.OnNextWorldUpdate(InitializeWorld);
EnumerateIfReady();
Check(!world.IsReady && playerReads == 0 && timerStarts == 0, "Cold load never reads players or starts timers before globals exist");
world.Unload();
EnumerateIfReady();
initialUpdate();
Check(!world.IsReady && playerReads == 0 && timerStarts == 0, "Failed-load cleanup and stale callbacks never access globals");

world.Load();
var beforeMapStart = world.OnNextWorldUpdate(InitializeWorld);
world.Suspend();
var mapStartUpdate = world.OnNextWorldUpdate(InitializeWorld);
beforeMapStart();
Check(!world.IsReady && playerReads == 0, "Map start invalidates initialization queued for the previous generation");
globalsInitialized = true;
mapStartUpdate();
Check(world.IsReady && playerReads == 1 && timerStarts == 1, "First valid world update opens player access and starts synchronization");
EnumerateIfReady();
Check(playerReads == 2, "Ready worlds support periodic player synchronization");

var obsoleteMapUpdate = world.OnNextWorldUpdate(InitializeWorld);
world.Suspend();
globalsInitialized = false;
EnumerateIfReady();
obsoleteMapUpdate();
Check(!world.IsReady && playerReads == 2 && timerStarts == 1, "Map end closes player access and cancels delayed callbacks");
var nextMapUpdate = world.OnNextWorldUpdate(InitializeWorld);
globalsInitialized = true;
nextMapUpdate();
Check(world.IsReady && playerReads == 3 && timerStarts == 2, "Next map resumes synchronization after its world update");

var beforeUnload = world.OnNextWorldUpdate(InitializeWorld);
world.Unload();
globalsInitialized = false;
EnumerateIfReady();
beforeUnload();
Check(!world.IsLoaded && !world.IsReady && playerReads == 3, "Unload cancels queued world initialization");
world.Load();
beforeUnload();
Check(!world.IsReady && playerReads == 3, "A callback from an unloaded lifetime cannot initialize a reloaded plugin");
var hotReloadUpdate = world.OnNextWorldUpdate(InitializeWorld);
globalsInitialized = true;
hotReloadUpdate();
Check(world.IsReady && playerReads == 4 && timerStarts == 3, "Hot reload initializes through a world update without needing a new map-start event");

var defaultMessages = MessageCatalog.CreateDefault();
Check(defaultMessages.Get("en", "noclip_enabled") == "[FakeCheats] Noclip enabled.", "English is available without an external language file");
Check(defaultMessages.Get("zhcn", "noclip_enabled") == "[FakeCheats] 穿墙已开启。", "Chinese translation remains readable as Unicode");
Check(MessageCatalog.NormalizeLanguage(" ZHCN ") == "zhcn", "Language selection accepts case and surrounding whitespace");
Check(defaultMessages.Get("EN", "switch_status", 0).Contains("fakecheats_enabled=0"), "Formatted English messages include current state");
foreach (var invalidLanguage in new string?[] { null, "", "en-US", "cn", "fr" })
{
    var rejected = false;
    try { MessageCatalog.NormalizeLanguage(invalidLanguage); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "Unsupported language is rejected before plugin initialization");
}

using (var messageResource = typeof(MessageCatalog).Assembly.GetManifestResourceStream("FakeCheats.messages.json")!)
using (var catalogDocument = JsonDocument.Parse(messageResource))
{
    foreach (var entry in catalogDocument.RootElement.EnumerateObject())
    {
        var en = entry.Value.GetProperty("en").GetString()!;
        var zhcn = entry.Value.GetProperty("zhcn").GetString()!;
        var argumentCount = CompositeFormat.Parse(en).MinimumArgumentCount;
        Check(CompositeFormat.Parse(zhcn).MinimumArgumentCount == argumentCount, $"Both translations have compatible placeholders: {entry.Name}");
        var arguments = Enumerable.Range(0, argumentCount).Select(i => (object?)i).ToArray();
        Check(!string.IsNullOrEmpty(defaultMessages.Get("en", entry.Name, arguments)), $"English catalog entry renders: {entry.Name}");
        Check(!string.IsNullOrEmpty(defaultMessages.Get("zhcn", entry.Name, arguments)), $"Chinese catalog entry renders: {entry.Name}");
    }
}

var languageTestDirectory = Path.Combine(Path.GetTempPath(), "FakeCheatsMessages-" + Guid.NewGuid().ToString("N"));
var languageTestPath = Path.Combine(languageTestDirectory, "messages.json");
try
{
    var generatedMessages = MessageCatalog.Load(languageTestPath);
    Check(File.Exists(languageTestPath), "First load creates the complete editable message catalog");
    Check(generatedMessages.Get("zhcn", "noclip_enabled") == defaultMessages.Get("zhcn", "noclip_enabled"), "Generated catalog preserves Chinese text");
    var customJson = """
    {
      "noclip_enabled": { "en": "Custom noclip on", "zhcn": "自定义穿墙开启" },
      "noclip_disabled": { "en": "Broken {", "zhcn": null },
      "noclip_alive_required": { "en": "Invalid {99}" },
      "switch_status": { "en": "Invalid numeric format: {0:Q}" },
      "status_sent": { "en": "Current value: {0}", "zhcn": null },
      "log_loaded": { "zhcn": "{0} 自定义启动" },
      "exec_submitted": null,
      "unused_custom_key": { "en": "Ignored", "zhcn": "忽略" }
    }
    """;
    File.WriteAllText(languageTestPath, customJson, new UTF8Encoding(false));
    var customMessages = MessageCatalog.Load(languageTestPath);
    Check(customMessages.Get("en", "noclip_enabled") == "Custom noclip on", "Custom English text is used");
    Check(customMessages.Get("zhcn", "noclip_enabled") == "自定义穿墙开启", "Custom Chinese text is used");
    Check(customMessages.Get("en", "noclip_disabled") == defaultMessages.Get("en", "noclip_disabled"), "Malformed braces use the built-in translation");
    Check(customMessages.Get("zhcn", "noclip_disabled") == defaultMessages.Get("zhcn", "noclip_disabled"), "Null translations use the selected language's built-in text");
    Check(customMessages.Get("en", "noclip_alive_required") == defaultMessages.Get("en", "noclip_alive_required"), "Unexpected placeholder indexes cannot break a callback");
    Check(customMessages.Get("en", "switch_status", 1) == defaultMessages.Get("en", "switch_status", 1), "Invalid numeric formats fall back at runtime");
    Check(customMessages.Get("en", "status_sent", 0) == "Current value: 0", "Custom placeholders receive arguments");
    Check(customMessages.Get("zhcn", "status_sent", 0) == defaultMessages.Get("zhcn", "status_sent", 0), "A partial English override does not replace the Chinese fallback");
    Check(customMessages.Get("zhcn", "log_loaded", "1.0.4") == "1.0.4 自定义启动", "Localized messages can rearrange placeholders");
    Check(customMessages.Get("en", "exec_submitted") == defaultMessages.Get("en", "exec_submitted"), "Null message entries cannot remove their fallback");
    Check(customMessages.Get("en", "command_denied") == defaultMessages.Get("en", "command_denied"), "Missing messages keep their built-in English text");
    Check(File.ReadAllText(languageTestPath) == customJson, "Loading never overwrites administrator translations");
    Check(MessageCatalog.CreateDefault().Get("en", "noclip_enabled") == "[FakeCheats] Noclip enabled.", "Custom overrides never mutate the shared defaults");

    File.WriteAllText(languageTestPath, "not valid JSON");
    var invalidJsonRejected = false;
    try { MessageCatalog.Load(languageTestPath); }
    catch (JsonException) { invalidJsonRejected = true; }
    Check(invalidJsonRejected, "Malformed JSON is reported for the plugin's built-in fallback handler");
}
finally
{
    if (File.Exists(languageTestPath)) File.Delete(languageTestPath);
    if (Directory.Exists(languageTestDirectory)) Directory.Delete(languageTestDirectory);
}

NativeNoclipTests.Run(Check);
DeferredConVarSyncTests.Run(Check);
Console.WriteLine($"PASS: {checks} command policy, world lifecycle, localization, native flag and deferred ConVar checks. Engine/client integration requires a running CS2 server.");
