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
using DpsInServerBar.Opener;
using DpsInServerBar.Tracking;
using DpsInServerBar.Ultimates;
using DpsInServerBar.Windows;

namespace DpsInServerBar;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/raidcompanion";
    private const string ShortCommand = "/rcomp";
    // Old name, kept (hidden from the help) so existing macros keep working.
    private const string LegacyCommand = "/dpsbar";

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
    [PluginService] internal static ITextureReadbackProvider TextureReadback { get; private set; } = null!;
    [PluginService] internal static IJobGauges JobGauges { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("DpsInServerBar");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly CompactWindow compactWindow;
    private readonly OpenerWindow openerWindow;
    private readonly OpenerCompactWindow openerCompactWindow;
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
        UI.Names.Anonymize = Configuration.HideNames;
        Loc.Set(Configuration.Language, PluginInterface.UiLanguage);

        var act = new ActClient(Log, () => playerName);
        var native = new NativeCombatTracker(GameInterop, ObjectTable, PartyList, Condition, ClientState, DataManager, PlayerState, Log);
        native.Mistakes.IgnoreTankbusters = Configuration.MistakesIgnoreTankbusters;
        Sources = new CombatSourceManager(Configuration, act, native);
        History = new PullHistory(PluginInterface.GetPluginConfigDirectory(), Log);
        Pulls = new PullTracker(this, DutyState, Condition, ChatGui);
        Boss = new BossTracker(ObjectTable, Condition, DataManager);
        FightLog = new FightLog(ObjectTable, PartyList, Condition, DataManager);
        Ultimates = new UltimateTracker(Configuration, ObjectTable, Condition, ClientState, DataManager);
        Ultimates.PhaseEnded += OnPhaseEnded;
        ContentCategories = new ContentCategories(DataManager);
        Fights = new FightCatalog(DataManager);
        Uptime = new UptimeTracker(ObjectTable, Condition, () => Ultimates.Current is { Started: true } p ? p.Number : null);
        Gauges = new GaugeTracker(JobGauges, ObjectTable, Condition, DataManager);
        native.LocalActionUsed += Gauges.OnActionUsed;
        native.LocalActionUsed += Uptime.OnActionUsed;
        Combos = new ComboTracker(ObjectTable, Condition, DataManager);
        native.LocalActionUsed += Combos.OnActionUsed;
        Positionals = new PositionalTracker(ObjectTable, Condition, DataManager, Log);
        native.LocalDamage += Positionals.OnHit;
        Dots = new DotTracker(ObjectTable, Condition, DataManager, () => Uptime.EnemyTargetable, () => Boss.Current?.EntityId ?? 0);
        native.TankSwaps.BossId = () => Boss.Current?.EntityId ?? 0;
        Percentiles = new PercentileService(Configuration, DataManager, PluginInterface.GetPluginConfigDirectory(), Log);
        OpenerTracker = new OpenerTracker(GameInterop, ObjectTable, Log);
        dtr = new DtrController(this, DtrBar);

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        compactWindow = new CompactWindow(this) { IsOpen = Configuration.LiveCompact };
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);
        openerWindow = new OpenerWindow(this);
        openerCompactWindow = new OpenerCompactWindow(this, openerWindow);
        windowSystem.AddWindow(compactWindow);
        windowSystem.AddWindow(openerWindow);
        windowSystem.AddWindow(openerCompactWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = Loc.T("Open the main window. Subcommands: config, toggle, reconnect, history, compact, opener [compact|full], preset <number|name|next>."),
        });
        CommandManager.AddHandler(ShortCommand, new CommandInfo(OnCommand) { HelpMessage = Loc.F("Short for {0}.", CommandName) });
        CommandManager.AddHandler(LegacyCommand, new CommandInfo(OnCommand) { ShowInHelp = false });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.LanguageChanged += OnLanguageChanged;
        ClientState.TerritoryChanged += OnTerritoryChanged;
        Framework.Update += OnFrameworkUpdate;

        Sources.Apply(reconnectAct: true);
    }

    public Configuration Configuration { get; }
    public CombatSourceManager Sources { get; }
    public PullHistory History { get; }
    public PullTracker Pulls { get; }
    public DtrController Bar => dtr;
    public BossTracker Boss { get; }
    public FightLog FightLog { get; }
    public UltimateTracker Ultimates { get; }
    public UptimeTracker Uptime { get; }

    public GaugeTracker Gauges { get; }
    public PositionalTracker Positionals { get; }
    public DotTracker Dots { get; }
    public ComboTracker Combos { get; }
    public ContentCategories ContentCategories { get; }
    public FightCatalog Fights { get; }
    public PercentileService Percentiles { get; }
    public OpenerTracker OpenerTracker { get; }
    public ActClient Act => Sources.Act;
    public uint CurrentTerritory => ClientState.TerritoryType;

    /// <summary>Changes each time you enter a zone: the pulls of one entry into a duty share it.</summary>
    public Guid InstanceRun { get; private set; } = Guid.NewGuid();

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.LanguageChanged -= OnLanguageChanged;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(ShortCommand);
        CommandManager.RemoveHandler(LegacyCommand);

        windowSystem.RemoveAllWindows();
        dtr.Dispose();
        Ultimates.PhaseEnded -= OnPhaseEnded;
        Pulls.Dispose();
        Percentiles.Dispose();
        OpenerTracker.Dispose();
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

    public bool OpenerOpen => openerWindow.IsOpen || openerCompactWindow.IsOpen;

    /// <summary>Opens the opener practice (in the view last used) or closes it.</summary>
    public void ToggleOpener()
    {
        if (OpenerOpen)
            CloseOpener();
        else if (Configuration.OpenerCompact)
            openerCompactWindow.IsOpen = true;
        else
            openerWindow.IsOpen = true;
    }

    /// <summary>Opens the opener practice in the given view (or closes it when that view is already open).</summary>
    public void ToggleOpenerView(bool compact)
    {
        if (compact ? openerCompactWindow.IsOpen : openerWindow.IsOpen)
            CloseOpener();
        else
            SetOpenerCompact(compact);
    }

    public void CloseOpener()
    {
        openerWindow.IsOpen = false;
        openerCompactWindow.IsOpen = false;
    }

    /// <summary>Switches the opener practice between its full window and its compact pop-up, keeping the try.</summary>
    public void SetOpenerCompact(bool compact)
    {
        Configuration.OpenerCompact = compact;
        Configuration.Save();
        openerCompactWindow.IsOpen = compact;
        openerWindow.IsOpen = !compact;
    }

    /// <summary>Your actions are only watched while one of the opener practice views is open.</summary>
    public void UpdateOpenerTracking()
    {
        var open = openerWindow.IsOpen || openerCompactWindow.IsOpen;
        OpenerTracker.Enabled = open;
        if (!open)
            openerWindow.ResetRun();
    }

    /// <summary>Opens or closes the compact meter (the full window closes when it opens).</summary>
    public void SetCompact(bool open)
    {
        compactWindow.IsOpen = open;
        Configuration.LiveCompact = open;
        Configuration.Save();
    }

    /// <summary>Opens the main window on a pull in History (chat summary link).</summary>
    public void ShowPull(Guid pullId) => mainWindow.ShowPull(pullId);

    /// <summary>Opens the main window from the compact meter (which stays open).</summary>
    public void ShowMainWindow(bool history = false)
    {
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

    private void OnLanguageChanged(string language) => Loc.Set(Configuration.Language, language);

    private void OnTerritoryChanged(uint territory) => InstanceRun = Guid.NewGuid();

    public void OnConfigChanged(bool reconnect = false)
    {
        Configuration.Save();
        UI.Theme.Configure(Configuration);
        UI.Names.Anonymize = Configuration.HideNames;
        Loc.Set(Configuration.Language, PluginInterface.UiLanguage);
        dtr.Invalidate();
        Sources.Native.Mistakes.IgnoreTankbusters = Configuration.MistakesIgnoreTankbusters;

        if (reconnect && !Uri.TryCreate(Configuration.WebSocketUrl, UriKind.Absolute, out _))
        {
            ChatGui.PrintError(Loc.F("[Raid Companion] Invalid address: {0}",Configuration.WebSocketUrl));
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
        Gauges.Update();
        Positionals.Update();
        Dots.Update();
        Combos.Update();
        FightLog.Update();

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

    // "/raidcompanion preset 2", "/raidcompanion preset Raid", "/raidcompanion preset next".
    private void OnPresetCommand(string arg)
    {
        var presets = Configuration.BarPresets;
        if (presets.Count == 0)
        {
            ChatGui.PrintError(Loc.T("[Raid Companion] No preset saved yet: create one in the settings, Display section."));
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
            ChatGui.PrintError(Loc.F("[Raid Companion] Unknown preset \"{0}\". Use a number from 1 to {1} or a preset name.",arg,presets.Count));
            return;
        }

        ApplyBarPreset(index);
        ChatGui.Print(Loc.F("[Raid Companion] Preset {0}: {1}",index + 1,presets[index].Name));
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
            case "opener":
                ToggleOpener();
                break;
            case "opener compact":
                ToggleOpenerView(compact: true);
                break;
            case "opener full":
                ToggleOpenerView(compact: false);
                break;
            default:
                ToggleMainUi();
                break;
        }
    }
}
