using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DpsInServerBar.Act;
using DpsInServerBar.Combat;
using DpsInServerBar.Display;
using DpsInServerBar.Fflogs;
using DpsInServerBar.Native;
using DpsInServerBar.Tracking;
using DpsInServerBar.Ultimates;
using DpsInServerBar.Windows;

namespace DpsInServerBar;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/dpsbar";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("DpsInServerBar");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly CompactWindow compactWindow;
    private readonly DtrController dtr;

    // Read on the game thread, consumed by the WebSocket thread when matching the player's name.
    private volatile string? playerName;
    private DateTime nextRefresh = DateTime.MinValue;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Configuration.Migrate())
            Configuration.Save();
        UI.Theme.Configure(Configuration);

        var act = new ActClient(Log, () => playerName);
        var native = new NativeCombatTracker(GameInterop, ObjectTable, PartyList, Condition, ClientState, DataManager, PlayerState, Log);
        Sources = new CombatSourceManager(Configuration, act, native);
        History = new PullHistory(PluginInterface.GetPluginConfigDirectory(), Log);
        Pulls = new PullTracker(this, DutyState, Condition, ChatGui);
        Boss = new BossTracker(ObjectTable, Condition);
        Ultimates = new UltimateTracker(Configuration, ObjectTable, Condition, ClientState, DataManager);
        Ultimates.PhaseEnded += OnPhaseEnded;
        ContentCategories = new ContentCategories(DataManager);
        Fights = new FightCatalog(DataManager);
        Uptime = new UptimeTracker(ObjectTable, Condition, () => Ultimates.Current is { Started: true } p ? p.Number : null);
        Percentiles = new PercentileService(Configuration, DataManager, PluginInterface.GetPluginConfigDirectory(), Log);
        dtr = new DtrController(this, DtrBar);

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        compactWindow = new CompactWindow(this) { IsOpen = Configuration.LiveCompact };
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(compactWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the DPS window. Subcommands: config, toggle, reconnect, history, compact, preset <number|name|next>.",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        Framework.Update += OnFrameworkUpdate;

        Sources.Apply(reconnectAct: true);
    }

    public Configuration Configuration { get; }
    public CombatSourceManager Sources { get; }
    public PullHistory History { get; }
    public PullTracker Pulls { get; }
    public DtrController Bar => dtr;
    public BossTracker Boss { get; }
    public UltimateTracker Ultimates { get; }
    public UptimeTracker Uptime { get; }
    public ContentCategories ContentCategories { get; }
    public FightCatalog Fights { get; }
    public PercentileService Percentiles { get; }
    public ActClient Act => Sources.Act;
    public uint CurrentTerritory => ClientState.TerritoryType;

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        CommandManager.RemoveHandler(CommandName);

        windowSystem.RemoveAllWindows();
        dtr.Dispose();
        Ultimates.PhaseEnded -= OnPhaseEnded;
        Pulls.Dispose();
        Percentiles.Dispose();
        Sources.Dispose();
    }

    /// <summary>Estimated FFLogs percentile of your rDPS in this snapshot, if it can be computed.</summary>
    public double? EstimatePercentile(CombatSnapshot? snapshot) =>
        snapshot?.You is { HasJob: true, HasRdps: true } you
            ? Percentiles.Estimate(CurrentTerritory, Boss.Current?.NameId ?? 0, you.Job, you.RDps)
            : null;

    public void ToggleMainUi() => mainWindow.Toggle();
    public void ToggleConfigUi() => configWindow.Toggle();

    public bool CompactOpen => compactWindow.IsOpen;

    /// <summary>Opens or closes the compact meter (the full window closes when it opens).</summary>
    public void SetCompact(bool open)
    {
        compactWindow.IsOpen = open;
        if (open)
            mainWindow.IsOpen = false;
        Configuration.LiveCompact = open;
        Configuration.Save();
    }

    /// <summary>From the compact meter back to the full window.</summary>
    public void ShowFullView(bool history = false)
    {
        SetCompact(false);
        if (history)
            mainWindow.ShowHistory();
        else
            mainWindow.IsOpen = true;
    }

    public void ToggleMetric()
    {
        Configuration.Metric = Configuration.Metric switch
        {
            DisplayMetric.Personal => DisplayMetric.Party,
            DisplayMetric.Party => DisplayMetric.Raid,
            _ => DisplayMetric.Personal,
        };
        OnConfigChanged();
    }

    public void OnConfigChanged(bool reconnect = false)
    {
        Configuration.Save();
        UI.Theme.Configure(Configuration);
        dtr.Invalidate();

        if (reconnect && !Uri.TryCreate(Configuration.WebSocketUrl, UriKind.Absolute, out _))
        {
            ChatGui.PrintError($"[DPS In Server Bar] Invalid address: {Configuration.WebSocketUrl}");
            return;
        }

        Sources.Apply(reconnect);
    }

    private void OnPhaseEnded(PhaseResult result)
    {
        if (Configuration.ChatSummary && Configuration.ChatSummaryPhases)
            Pulls.PrintPhaseSummary(result);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        Sources.Tick();
        Uptime.Update();

        // The bar only needs a few refreshes per second; sources publish roughly once per second.
        var now = DateTime.UtcNow;
        if (now < nextRefresh)
            return;
        nextRefresh = now.AddMilliseconds(250);

        playerName = PlayerState.IsLoaded ? PlayerState.CharacterName : null;

        Boss.Update();
        Ultimates.Update();

        var source = Sources.Current;
        Pulls.Update(Sources.Latest, source.Name);
        dtr.Update();
    }

    /// <summary>Switches the server bar to a saved preset (0-based index).</summary>
    public void ApplyBarPreset(int index)
    {
        if (index < 0 || index >= Configuration.BarPresets.Count)
            return;

        Configuration.BarPresets[index].ApplyTo(Configuration);
        Configuration.ActiveBarPreset = index;
        OnConfigChanged();
    }

    // "/dpsbar preset 2", "/dpsbar preset Raid", "/dpsbar preset next".
    private void OnPresetCommand(string arg)
    {
        var presets = Configuration.BarPresets;
        if (presets.Count == 0)
        {
            ChatGui.PrintError("[DPS In Server Bar] No preset saved yet: create one in the settings, Display section.");
            return;
        }

        int index;
        if (arg.Length == 0 || arg.Equals("next", StringComparison.OrdinalIgnoreCase))
            index = (Configuration.ActiveBarPreset + 1) % presets.Count;
        else if (int.TryParse(arg, out var number))
            index = number - 1;
        else
            index = presets.FindIndex(p => p.Name.Equals(arg, StringComparison.OrdinalIgnoreCase));

        if (index < 0 || index >= presets.Count)
        {
            ChatGui.PrintError($"[DPS In Server Bar] Unknown preset \"{arg}\". Use a number from 1 to {presets.Count} or a preset name.");
            return;
        }

        ApplyBarPreset(index);
        ChatGui.Print($"[DPS In Server Bar] Preset {index + 1}: {presets[index].Name}");
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (trimmed.StartsWith("preset", StringComparison.OrdinalIgnoreCase)
            && (trimmed.Length == 6 || char.IsWhiteSpace(trimmed[6])))
        {
            OnPresetCommand(trimmed[6..].Trim());
            return;
        }

        switch (trimmed.ToLowerInvariant())
        {
            case "config":
            case "settings":
                ToggleConfigUi();
                break;
            case "toggle":
                ToggleMetric();
                break;
            case "reconnect":
                OnConfigChanged(reconnect: true);
                break;
            case "history":
                mainWindow.ShowHistory();
                break;
            case "compact":
                SetCompact(!compactWindow.IsOpen);
                break;
            default:
                ToggleMainUi();
                break;
        }
    }
}
