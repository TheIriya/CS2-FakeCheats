using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace FakeCheats;

[MinimumApiVersion(379)]
public sealed class FakeCheatsPlugin : BasePlugin, IPluginConfig<FakeCheatsConfig>
{
    public override string ModuleName => "FakeCheats";
    public override string ModuleVersion => "1.0.8";
    public override string ModuleAuthor => "Community";
    public override string ModuleDescription => "Client sv_cheats spoof with a server command whitelist and @css/root bypass.";

    public FakeCheatsConfig Config { get; set; } = new();
    // Keep the switch in managed memory: dynamic engine ConVar creation can
    // fail during server startup on some CSS builds. The registered command
    // preserves the same server.cfg, console and RCON syntax without it.
    private int _fakeCheatsEnabled = 1;
    private bool FakeCheatsOn => _fakeCheatsEnabled == 1;
    private CommandPolicy _policy = new([]);
    private MessageCatalog _messages = MessageCatalog.CreateDefault();
    private ConVar<bool>? _svCheats;
    private readonly WorldLifecycle _world = new();
    private readonly DeferredConVarSync _conVarSync = new();
    private readonly NativeNoclipAccess _nativeNoclip = new();
    private readonly Dictionary<int, DateTime> _lastDenial = [];
    private sealed record NoclipSnapshot(uint PawnHandle, MoveType_t MoveType, MoveType_t ActualMoveType);
    private readonly Dictionary<int, NoclipSnapshot> _noclip = [];
    private CounterStrikeSharp.API.Modules.Timers.Timer? _replicateTimer;

    public void OnConfigParsed(FakeCheatsConfig config)
    {
        config.Language = MessageCatalog.NormalizeLanguage(config.Language);
        if (!float.IsFinite(config.ReplicateIntervalSeconds)
            || config.ReplicateIntervalSeconds is < 1 or > 60)
            throw new ArgumentException("ReplicateIntervalSeconds must be between 1 and 60.");

        _policy = new CommandPolicy(config.AdditionalAllowedServerCommands
            ?? throw new ArgumentException("AdditionalAllowedServerCommands must be an array."));
        Config = config;
        if (_world.IsReady) StartReplicationTimer();
    }

    public override void Load(bool hotReload)
    {
        LoadMessages();
        _svCheats = ConVar<bool>.Find("sv_cheats")
            ?? throw new InvalidOperationException("sv_cheats was not found; refusing to load.");

        RegisterListener<Listeners.OnConVarChanged>(OnConVarChanged);
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _world.Suspend();
            _conVarSync.Start();
            StopReplicationTimer();
            KeepServerCheatsOff();
            _lastDenial.Clear();
            ScheduleWorldInitialization();
        });
        RegisterListener<Listeners.OnMapEnd>(() =>
        {
            _conVarSync.Cancel();
            foreach (var player in GetReadyPlayers()) RestoreNoclip(player);
            _noclip.Clear();
            _world.Suspend();
            StopReplicationTimer();
            _lastDenial.Clear();
        });
        RegisterListener<Listeners.OnClientDisconnect>(slot =>
        {
            _lastDenial.Remove(slot);
            _noclip.Remove(slot);
        });
        RegisterListener<Listeners.OnTick>(CheckNoclipPermissions);
        RegisterEventHandler<EventPlayerConnectFull>(OnConnectFull);
        RegisterEventHandler<EventPlayerSpawn>((ev, _) =>
        {
            if (ev.Userid is { IsValid: true } player) RestoreNoclip(player);
            Replicate(ev.Userid);
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerDeath>((ev, _) =>
        {
            if (ev.Userid is { IsValid: true } player) RestoreNoclip(player);
            return HookResult.Continue;
        });
        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            ReplicateAll();
            return HookResult.Continue;
        });

        // null means the global listener, not a wildcard command name.
        AddCommandListener(null, OnServerCommand, HookMode.Pre);
        AddCommand("css_fc_status", Message("description_status"), OnStatus);
        AddCommand("css_fc_sync", Message("description_sync"), OnSync);
        AddCommand("css_fc_exec", Message("description_exec"), OnRootExec);
        AddCommand("css_fc_noclip", Message("description_noclip"), OnRootNoclip);
        AddCommand("css_fc_enabled", Message("description_enabled"), OnSetEnabled);
        AddCommand("fakecheats_enabled", Message("description_enabled"), OnSetEnabled);

        // Install the permission gate before removing noclip's engine cheat
        // flag. If the ABI is unavailable, retain the plugin command fallback.
        try
        {
            _nativeNoclip.Enable();
            Logger.LogInformation("{Message}", Message("log_native_noclip_ready"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException
            or System.ComponentModel.Win32Exception or ArgumentException)
        {
            _nativeNoclip.Dispose();
            Logger.LogWarning(ex, "{Message}", Message("log_native_noclip_failed"));
        }

        _world.Load();
        _conVarSync.Start();
        KeepServerCheatsOff();
        // Queueing this managed callback does not read CS2's global variables.
        // Also handles a manual load/hot reload on an already running map.
        ScheduleWorldInitialization();
        Logger.LogInformation("{Message}", Message("log_loaded", ModuleVersion));
    }

    private string Message(string key, params object?[] arguments) =>
        _messages.Get(Config.Language, key, arguments);

    private void LoadMessages()
    {
        var path = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..",
            "configs", "plugins", "FakeCheats", "messages.json"));
        try
        {
            _messages = MessageCatalog.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _messages = MessageCatalog.CreateDefault();
            Logger.LogWarning(ex, "{Message}", Message("log_language_file_failed", path));
        }
    }

    private void ScheduleWorldInitialization()
    {
        Server.NextWorldUpdate(_world.OnNextWorldUpdate(() =>
        {
            KeepServerCheatsOff();
            ReplicateAll();
            if (_world.IsReady) StartReplicationTimer();
        }));
    }

    private void StartReplicationTimer()
    {
        StopReplicationTimer();
        if (!_world.IsReady) return;
        _replicateTimer = AddTimer(Config.ReplicateIntervalSeconds, () =>
        {
            if (!_world.IsReady) return;
            KeepServerCheatsOff();
            ReplicateAll();
        }, TimerFlags.REPEAT);
    }

    private void StopReplicationTimer()
    {
        _replicateTimer?.Kill();
        _replicateTimer = null;
    }

    private void OnConVarChanged(ConVarBase conVar)
    {
        if (!conVar.Name.Equals("sv_cheats", StringComparison.OrdinalIgnoreCase)) return;
        // Writing from this native callback can re-enter the engine's change
        // dispatch. Wait until it has returned before correcting and syncing.
        _conVarSync.Request(action => Server.NextWorldUpdate(action), () =>
        {
            KeepServerCheatsOff();
            if (_world.IsReady) ReplicateAll();
        });
    }

    private void KeepServerCheatsOff()
    {
        if (_svCheats == null || !_svCheats.Value) return;
        Logger.LogWarning("{Message}", Message("log_real_cheats_reset"));
        _svCheats.Value = false;
    }

    private static bool IsReady(CCSPlayerController? player) => player is
    {
        IsValid: true, IsBot: false, IsHLTV: false,
        Connected: PlayerConnectedState.Connected
    };

    private static bool IsRoot(CCSPlayerController player) =>
        AdminManager.PlayerHasPermissions(player, "@css/root");

    private void Replicate(CCSPlayerController? player)
    {
        if (_world.IsReady && IsReady(player))
            player!.ReplicateConVar("sv_cheats", FakeCheatsOn ? "1" : "0");
    }

    private IReadOnlyList<CCSPlayerController> GetReadyPlayers()
    {
        if (!_world.IsReady) return [];
        try
        {
            return Utilities.GetPlayers();
        }
        catch (NativeException ex) when (ex.Message.Contains(
            "Global Variables not initialized yet", StringComparison.Ordinal))
        {
            // World teardown can invalidate globals before the map-end listener.
            // Do not suppress any other native error.
            _world.Suspend();
            Logger.LogDebug("{Message}", Message("log_sync_postponed"));
            return [];
        }
    }

    private void ReplicateAll()
    {
        foreach (var player in GetReadyPlayers()) Replicate(player);
    }

    private HookResult OnConnectFull(EventPlayerConnectFull ev, GameEventInfo info)
    {
        Replicate(ev.Userid);
        return HookResult.Continue;
    }

    private HookResult OnServerCommand(CCSPlayerController? player, CommandInfo command)
    {
        // Server console and RCON retain their normal administration access.
        if (player == null) return HookResult.Continue;
        if (!player.IsValid) return HookResult.Stop;
        if (IsRoot(player))
        {
            // The native command's cheat flag is removed while this gate is
            // active, so root requests reach our direct movement handler.
            if (command.GetArg(0).Equals("noclip", StringComparison.OrdinalIgnoreCase))
            {
                OnRootNoclip(player, command);
                return HookResult.Stop;
            }
            return HookResult.Continue;
        }

        var args = Enumerable.Range(0, command.ArgCount).Select(command.GetArg).ToArray();
        if (_policy.IsAllowed(false, args)) return HookResult.Continue;

        var now = DateTime.UtcNow;
        if (!_lastDenial.TryGetValue(player.Slot, out var previous)
            || now - previous >= TimeSpan.FromSeconds(2))
        {
            _lastDenial[player.Slot] = now;
            player.PrintToConsole(Message("command_denied"));
        }
        // Stop also prevents other CSS command callbacks handling this request.
        return HookResult.Stop;
    }

    private void OnStatus(CCSPlayerController? player, CommandInfo command)
    {
        var role = Message(player == null ? "role_server" : IsRoot(player) ? "role_root" : "role_whitelist");
        var spoof = _world.IsReady ? Message("status_sent", FakeCheatsOn ? 1 : 0) : Message("status_pending");
        command.ReplyToCommand(Message("status_summary", _fakeCheatsEnabled, _svCheats?.Value == true ? 1 : 0, spoof, role));
    }

    private void OnSync(CCSPlayerController? player, CommandInfo command)
    {
        if (!_world.IsReady)
        {
            command.ReplyToCommand(Message("world_pending"));
            return;
        }
        if (player == null) ReplicateAll();
        else Replicate(player);
        command.ReplyToCommand(Message("sync_done", FakeCheatsOn ? 1 : 0));
    }

    private void OnSetEnabled(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null && (!IsReady(player) || !IsRoot(player)))
        {
            command.ReplyToCommand(Message("switch_denied"));
            return;
        }

        if (command.ArgCount == 1)
        {
            command.ReplyToCommand(Message("switch_status", _fakeCheatsEnabled));
            return;
        }
        if (command.ArgCount != 2 || command.GetArg(1) is not ("0" or "1"))
        {
            command.ReplyToCommand(Message("switch_usage", command.GetArg(0)));
            return;
        }

        _fakeCheatsEnabled = command.GetArg(1) == "1" ? 1 : 0;
        // Configuration commands may run before player enumeration is safe.
        // World initialization and every later sync read the latest value.
        if (_world.IsReady) ReplicateAll();
        Logger.LogInformation("{Message}", Message("log_switch_changed", _fakeCheatsEnabled));
        command.ReplyToCommand(Message("switch_changed", Message(FakeCheatsOn ? "state_enabled" : "state_disabled")));
    }

    private void OnRootNoclip(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsReady(player) || !IsRoot(player!))
        {
            command.ReplyToCommand(Message("noclip_denied"));
            return;
        }
        if (command.ArgCount != 1)
        {
            command.ReplyToCommand(Message("noclip_usage"));
            return;
        }
        ToggleNoclip(player!, command);
    }

    private void ToggleNoclip(CCSPlayerController player, CommandInfo command)
    {
        var pawn = player.PlayerPawn.Value;
        if (!_world.IsReady || !player.PawnIsAlive || pawn is not { IsValid: true })
        {
            command.ReplyToCommand(Message("noclip_alive_required"));
            return;
        }

        if (pawn.MoveType == MoveType_t.MOVETYPE_NOCLIP || pawn.ActualMoveType == MoveType_t.MOVETYPE_NOCLIP)
        {
            if (!RestoreNoclip(player))
                SetMovement(pawn, MoveType_t.MOVETYPE_WALK, MoveType_t.MOVETYPE_WALK);
            command.ReplyToCommand(Message("noclip_disabled"));
            return;
        }

        _noclip[player.Slot] = new NoclipSnapshot(player.PlayerPawn.Raw, pawn.MoveType, pawn.ActualMoveType);
        SetMovement(pawn, MoveType_t.MOVETYPE_NOCLIP, MoveType_t.MOVETYPE_NOCLIP);
        command.ReplyToCommand(Message("noclip_enabled"));
    }

    private static void SetMovement(CCSPlayerPawn pawn, MoveType_t moveType, MoveType_t actualMoveType)
    {
        // Updating both schema fields is required: MoveType alone can leave the
        // server's actual movement simulation in walking mode.
        pawn.MoveType = moveType;
        pawn.ActualMoveType = actualMoveType;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    private bool RestoreNoclip(CCSPlayerController player)
    {
        if (!_noclip.Remove(player.Slot, out var previous)) return false;
        var pawn = player.PlayerPawn.Value;
        if (player.PlayerPawn.Raw != previous.PawnHandle || pawn is not { IsValid: true }) return false;
        if (pawn.MoveType != MoveType_t.MOVETYPE_NOCLIP && pawn.ActualMoveType != MoveType_t.MOVETYPE_NOCLIP)
            return false;
        SetMovement(pawn, previous.MoveType, previous.ActualMoveType);
        return true;
    }

    private void CheckNoclipPermissions()
    {
        if (!_world.IsReady || _noclip.Count == 0) return;
        foreach (var (slot, previous) in _noclip.ToArray())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (!IsReady(player) || player!.PlayerPawn.Raw != previous.PawnHandle)
            {
                _noclip.Remove(slot);
                continue;
            }
            var pawn = player.PlayerPawn.Value;
            if (pawn is not { IsValid: true }
                || (pawn.MoveType != MoveType_t.MOVETYPE_NOCLIP && pawn.ActualMoveType != MoveType_t.MOVETYPE_NOCLIP))
            {
                _noclip.Remove(slot);
                continue;
            }
            if (!player.PawnIsAlive || !IsRoot(player)) RestoreNoclip(player);
        }
    }

    private void OnRootExec(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsReady(player) || !IsRoot(player!))
        {
            command.ReplyToCommand(Message("exec_denied"));
            return;
        }

        var input = command.ArgString.Trim();
        var name = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!CommandPolicy.IsSingleCommand(input) || !CommandPolicy.IsCommandName(name)
            || name!.Equals("css_fc_exec", StringComparison.OrdinalIgnoreCase))
        {
            command.ReplyToCommand(Message("exec_usage"));
            return;
        }

        if (name!.Equals("noclip", StringComparison.OrdinalIgnoreCase))
        {
            if (command.ArgCount != 2)
                command.ReplyToCommand(Message("exec_noclip_usage"));
            else
                ToggleNoclip(player!, command);
            return;
        }

        // Native dispatch preserves player context without changing sv_cheats.
        // This does not execute client-local rendering commands or ConVars.
        player!.ExecuteClientCommandFromServer(input);
        command.ReplyToCommand(Message("exec_submitted"));
    }

    public override void Unload(bool hotReload)
    {
        // A failed early load must not enumerate players again while unloading.
        // Take a snapshot only if a world update previously opened the gate.
        var players = GetReadyPlayers();
        _conVarSync.Cancel();
        // Restore the engine's protection before CSS removes command hooks.
        _nativeNoclip.Dispose();
        _world.Unload();
        StopReplicationTimer();
        KeepServerCheatsOff();
        foreach (var player in players)
        {
            if (IsReady(player))
            {
                RestoreNoclip(player);
                player.ReplicateConVar("sv_cheats", "0");
            }
        }
        _noclip.Clear();
        _lastDenial.Clear();
    }
}
