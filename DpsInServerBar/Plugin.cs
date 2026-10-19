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

    private readonly WindowSystem windowSystem = new("DpsInServerBar");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly DtrController dtr;

    // Read on the game thread, consumed by the WebSocket thread when matching the player's name.
    private volatile string? playerName;
    private DateTime nextRefresh = DateTime.MinValue;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Configuration.Migrate())
            Configuration.Save();

        var act = new ActClient(Log, () => playerName);
        var native = new NativeCombatTracker(GameInterop, ObjectTable, PartyList, Condition, ClientState, DataManager, PlayerState, Log);
        Sources = new CombatSourceManager(Configuration, act, native);
        History = new PullHistory(PluginInterface.GetPluginConfigDirectory(), Log);
        Pulls = new PullTracker(this, DutyState, Condition, ChatGui);
        Boss = new BossTracker(ObjectTable, Condition);
        Percentiles = new PercentileService(Configuration, DataManager, PluginInterface.GetPluginConfigDirectory(), Log);
        dtr = new DtrController(this, DtrBar);

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the DPS window. Subcommands: config, toggle, reconnect, history.",
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
    public BossTracker Boss { get; }
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

    public void ToggleMetric()
    {
        Configuration.Metric = Configuration.Metric switch
        {
            DisplayMetric.Personal => DisplayMetric.Party,
            DisplayMetric.Party when Configuration.ComputeRdps => DisplayMetric.Raid,
            _ => DisplayMetric.Personal,
        };
        OnConfigChanged();
    }

    public void OnConfigChanged(bool reconnect = false)
    {
        Configuration.Save();
        dtr.Invalidate();

        if (reconnect && !Uri.TryCreate(Configuration.WebSocketUrl, UriKind.Absolute, out _))
        {
            ChatGui.PrintError($"[DPS In Server Bar] Invalid address: {Configuration.WebSocketUrl}");
            return;
        }

        Sources.Apply(reconnect);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        Sources.Tick();

        // The bar only needs a few refreshes per second; sources publish roughly once per second.
        var now = DateTime.UtcNow;
        if (now < nextRefresh)
            return;
        nextRefresh = now.AddMilliseconds(250);

        playerName = PlayerState.IsLoaded ? PlayerState.CharacterName : null;

        Boss.Update();

        var source = Sources.Current;
        Pulls.Update(Sources.Latest, source.Name);
        dtr.Update();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
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
            default:
                ToggleMainUi();
                break;
        }
    }
}
