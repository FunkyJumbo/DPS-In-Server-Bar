using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DpsInServerBar.Opener;
using DpsInServerBar.UI;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;

namespace DpsInServerBar.Windows;

/// <summary>
/// Opener practice: pick an opener of your current job, press Start (or just use its first action), then each action
/// used in the right order greys its box out. Big boxes on the track are GCDs, small ones above are oGCDs.
/// </summary>
public sealed class OpenerWindow : Window
{
    private readonly Plugin plugin;
    private readonly Dictionary<uint, (uint Icon, string Name)> actions = new();
    private readonly Dictionary<string, (uint ItemId, uint Icon, string Name)> potions = new();
    private IDisposable? themeScope;
    private OpenerRun? run;
    private bool countdownWasActive;
    private float countdownLeft;

    public OpenerWindow(Plugin plugin)
        : base("Opener practice###DpsInServerBarOpener", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        Size = new Vector2(760, 300);
        SizeCondition = ImGuiCond.FirstUseEver;
        AllowBackgroundBlur = false;
        plugin.OpenerTracker.ActionUsed += OnActionUsed;
    }

    /// <summary>Height the content took on the last frame: the window fits it, only its width can be changed.</summary>
    private float contentHeight = 300;

    /// <summary>Width of the whole opener on a single row, from the last frame: the widest the window may get.</summary>
    private float timelineWidth;

    private float lastMaxWidth;
    private float windowWidth;

    public override void OnOpen() => plugin.UpdateOpenerTracking();

    public override void OnClose() => plugin.UpdateOpenerTracking();

    /// <summary>Drops the current try, once neither the window nor its compact view is open.</summary>
    public void ResetRun() => run = null;

    public override void PreDraw()
    {
        themeScope = Theme.Push();
        BgAlpha = plugin.Configuration.OpenerOpacity; // the background only, not the icons and text

        // Stretch it sideways to fit a long opener on fewer rows (at most the whole opener on one row); its height
        // always follows the content.
        var scale = ImGuiHelpers.GlobalScale;
        var height = contentHeight / scale;
        var maxWidth = timelineWidth > 0 ? Math.Max(800, (timelineWidth + ImGui.GetStyle().WindowPadding.X * 2) / scale) : 4000;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(800, height),
            MaximumSize = new Vector2(maxWidth, height),
        };

        // At its widest when the opener grows (pre-pull or tincture turned on): follow it, so it stays on one row.
        if (maxWidth > lastMaxWidth + 0.5f && lastMaxWidth > 0 && windowWidth / scale >= lastMaxWidth - 1)
        {
            Size = new Vector2(maxWidth, height);
            SizeCondition = ImGuiCond.Always;
        }
        else if (SizeCondition == ImGuiCond.Always)
        {
            Size = null;
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        lastMaxWidth = maxWidth;
    }

    public override void PostDraw()
    {
        themeScope?.Dispose();
        themeScope = null;
    }

    private void OnActionUsed(byte type, uint id) => run?.OnAction(type, id);

    public override void Draw()
    {
        DrawContent();
        windowWidth = ImGui.GetWindowWidth();
        contentHeight = ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + ImGui.GetStyle().WindowPadding.Y;
    }

    /// <summary>
    /// The opener selected for the current job, with its try ready (and following the game countdown). Null, after
    /// drawing why, when there is none.
    /// </summary>
    private OpenerDefinition? Prepare(out string job, out List<OpenerDefinition> openers)
    {
        var config = plugin.Configuration;
        job = CurrentJob();
        openers = OpenerDefinitions.ForJob(job).ToList();
        if (!plugin.OpenerTracker.Available)
        {
            Theme.Hint("Unavailable: the game hook could not be created (see /xllog).");
            return null;
        }

        if (openers.Count == 0)
        {
            run = null;
            Theme.Hint($"No opener for {(job.Length > 0 ? job : "this job")} yet. Available: "
                       + string.Join(", ", OpenerDefinitions.All.Select(o => o.Job).Distinct()) + ".");
            return null;
        }

        var selectedId = config.OpenerSelected.GetValueOrDefault(job);
        var opener = openers.FirstOrDefault(o => o.Id == selectedId) ?? openers[0];
        if (run is null || run.Opener != opener || run.Prepull != config.OpenerPrepull || run.Potion != config.OpenerPotion)
            run = NewRun(opener);
        FollowGameCountdown(opener);
        AutoClose();
        return opener;
    }

    /// <summary>
    /// Compact view (its own pop-up window): the opener's boxes on one row, Start / Reset and the way back to the
    /// full window.
    /// </summary>
    public void DrawCompact()
    {
        if (Prepare(out _, out _) is not { } opener)
        {
            if (Theme.Chip("Full view", false))
                plugin.SetOpenerCompact(false);
            return;
        }

        DrawTimeline(run!, singleRow: true, Math.Clamp(plugin.Configuration.OpenerCompactScale, 0.5f, 1.5f));

        var transparent = plugin.Configuration.OpenerOpacity < 0.5f;
        if (Theme.Chip(run!.Started ? "Reset" : "Start", run.Started, transparent))
        {
            if (run.Started)
                run = NewRun(opener);
            else
                StartRun(opener);
        }

        ImGui.SameLine();
        if (Theme.Chip("Full view", false, transparent))
            plugin.SetOpenerCompact(false);

        // Size: a small pop-up with the usual steps.
        var config = plugin.Configuration;
        ImGui.SameLine();
        if (Theme.Chip($"{Math.Round(config.OpenerCompactScale * 100)}%", false, transparent))
            ImGui.OpenPopup("##openercompactsize");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Size of this window");

        using var popup = ImRaii.Popup("##openercompactsize");
        if (!popup)
            return;

        ImGui.SetWindowFontScale(1f);
        foreach (var percent in new[] { 50, 75, 100, 125, 150 })
        {
            if (ImGui.Selectable($"{percent}%", Math.Abs(config.OpenerCompactScale * 100 - percent) < 0.5f))
            {
                config.OpenerCompactScale = percent / 100f;
                config.Save();
            }
        }
    }

    /// <summary>Start: the in-game /countdown when the game allows it, else a countdown in the window.</summary>
    private void StartRun(OpenerDefinition opener)
    {
        var config = plugin.Configuration;
        var gameCountdown = UseGameCountdown(config);
        run = NewRun(opener);
        if (gameCountdown)
            SendChatCommand($"/countdown {Math.Clamp(config.OpenerCountdown, 5, 30)}"); // picked up by FollowGameCountdown
        else
            run.Start(config.OpenerCountdown);
    }

    private void DrawContent()
    {
        var config = plugin.Configuration;
        if (Prepare(out var job, out var openers) is not { } opener || run is null)
            return;

        DrawControls(config, job, openers, opener);
        ImGui.Dummy(Theme.Px(new Vector2(0, 4)));
        DrawTimeline(run);
        ImGui.Dummy(Theme.Px(new Vector2(0, 2)));
        DrawStatus(run);
        if (!run.Started || run.Clock < 0)
            DrawReadiness(run);
        DrawNotes(opener);
    }

    private void DrawControls(Configuration config, string job, List<OpenerDefinition> openers, OpenerDefinition opener)
    {
        JobInfo.DrawIcon(job, ImGui.GetFrameHeight());
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Theme.Px(240));
        using (var combo = ImRaii.Combo("##opener", opener.Name))
        {
            if (combo)
            {
                foreach (var o in openers)
                {
                    if (ImGui.Selectable(o.Name, o == opener))
                    {
                        config.OpenerSelected[job] = o.Id;
                        config.Save();
                    }
                }
            }
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(opener.Source);

        ImGui.SameLine();
        var gameCountdown = UseGameCountdown(config);
        if (Theme.PrimaryButton(run!.Started ? "Restart" : "Start"))
            StartRun(opener);

        if (ImGui.IsItemHovered())
        {
            var how = gameCountdown
                ? $"Starts the in-game /countdown {config.OpenerCountdown}."
                : $"Starts a {config.OpenerCountdown} s countdown in this window"
                  + (config.OpenerGameCountdown ? " (in combat or during a countdown, the game refuses /countdown)." : ".");
            ImGui.SetTooltip(how + "\nYou can also just use the first action: the run starts with it.");
        }

        ImGui.SameLine();
        if (Theme.GhostButton("Reset"))
            run = NewRun(opener);

        ImGui.SameLine();
        var countdown = config.OpenerCountdown;
        ImGui.SetNextItemWidth(Theme.Px(130));
        if (ImGui.SliderInt("##countdown", ref countdown, 5, 30, "Countdown %d s"))
            config.OpenerCountdown = countdown;
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();

        // Right-aligned: background opacity, then Compact at the far right.
        ImGui.SameLine();
        var style = ImGui.GetStyle();
        var sliderWidth = Theme.Px(150);
        var compactWidth = ImGui.CalcTextSize("Compact").X + style.FramePadding.X * 2;
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - sliderWidth - style.ItemSpacing.X - compactWidth;
        if (right > ImGui.GetCursorPosX())
            ImGui.SetCursorPosX(right);

        var opacity = (int)Math.Round(config.OpenerOpacity * 100);
        ImGui.SetNextItemWidth(sliderWidth);
        if (ImGui.SliderInt("##openeropacity", ref opacity, 0, 100, "Background %d%%"))
            config.OpenerOpacity = opacity / 100f;
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opacity of the window's background only: icons and text stay fully visible.");

        ImGui.SameLine();
        if (Theme.PrimaryButton("Compact"))
            plugin.SetOpenerCompact(true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only the opener's icons, Start / Reset and a way back, in a small pop-up window.");

        var prepull = config.OpenerPrepull;
        if (ImGui.Checkbox("Pre-pull", ref prepull))
        {
            config.OpenerPrepull = prepull;
            config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Include the actions used before the pull. Off: the run starts with the first action after the pull.");

        ImGui.SameLine(0, Theme.Px(14));
        var potion = config.OpenerPotion;
        if (ImGui.Checkbox("Tincture", ref potion))
        {
            config.OpenerPotion = potion;
            config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Include the tincture. Off: using an item is ignored.");

        ImGui.SameLine(0, Theme.Px(14));
        var useGame = config.OpenerGameCountdown;
        if (ImGui.Checkbox("Use /countdown", ref useGame))
        {
            config.OpenerGameCountdown = useGame;
            config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Start launches the in-game /countdown (seen by your party), for a real pull on a striking dummy.\n"
                             + "In combat the game refuses it: the countdown then runs in this window only.\n"
                             + "A /countdown started by you or anyone in the party also starts a run.");
        }

        ImGui.SameLine(0, Theme.Px(14));
        var autoClose = config.OpenerAutoClose;
        if (ImGui.Checkbox("Auto close", ref autoClose))
        {
            config.OpenerAutoClose = autoClose;
            config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Closes the window by itself once the opener is done, or {config.OpenerAutoCloseSeconds} s after its first action.");

        using (ImRaii.Disabled(!config.OpenerAutoClose))
        {
            ImGui.SameLine();
            var seconds = config.OpenerAutoCloseSeconds;
            ImGui.SetNextItemWidth(Theme.Px(120));
            if (ImGui.SliderInt("##autoclose", ref seconds, 10, 120, "after %d s"))
                config.OpenerAutoCloseSeconds = seconds;
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save();
        }
    }

    /// <summary>
    /// Auto close (option): a few seconds after the opener is done (to see the result), or the chosen time after its
    /// first action.
    /// </summary>
    private void AutoClose()
    {
        var config = plugin.Configuration;
        if (!config.OpenerAutoClose || run is null)
            return;

        var now = DateTime.UtcNow;
        var done = run.FinishedUtc is { } finished && (now - finished).TotalSeconds >= 3;
        var timedOut = run.FirstActionUtc is { } first && (now - first).TotalSeconds >= config.OpenerAutoCloseSeconds;
        if (done || timedOut)
            plugin.CloseOpener();
    }

    /// <summary>The game only accepts /countdown out of combat, and not while one is running.</summary>
    private bool UseGameCountdown(Configuration config) =>
        config.OpenerGameCountdown && !Plugin.Condition[ConditionFlag.InCombat] && !countdownWasActive;

    private static unsafe void SendChatCommand(string command)
    {
        var message = Utf8String.FromString(command);
        try
        {
            UIModule.Instance()->ProcessChatBoxEntry(message);
        }
        finally
        {
            message->Dtor(true);
        }
    }


    /// <summary>
    /// Before the pull: whether everything is ready (out of combat, full MP, the opener's cooldowns and charges back
    /// up, the tincture's recast over), or what is still missing.
    /// </summary>
    private unsafe void DrawReadiness(OpenerRun current)
    {
        var problems = new List<string>();
        if (Plugin.Condition[ConditionFlag.InCombat])
            problems.Add("in combat");

        if (Plugin.ObjectTable.LocalPlayer is { } player && player.MaxMp > 0 && player.CurrentMp < player.MaxMp)
            problems.Add($"MP {player.CurrentMp:N0} / {player.MaxMp:N0}");

        var manager = ActionManager.Instance();
        if (manager != null)
        {
            var level = Plugin.ObjectTable.LocalPlayer?.Level ?? 100;
            foreach (var group in current.Steps.Where(s => s.Kind != StepKind.Potion).GroupBy(s => ActionNames.Find(s.Names[0])))
            {
                var id = group.Key;
                if (id == 0)
                    continue;

                var left = manager->GetRecastTime(ActionType.Action, id) - manager->GetRecastTimeElapsed(ActionType.Action, id);
                var maxCharges = ActionManager.GetMaxCharges(id, level);
                if (maxCharges > 1)
                {
                    // Charges: the opener needs as many as it uses (e.g. both Shadowbringers).
                    var needed = Math.Min(group.Count(), (int)maxCharges);
                    var charges = manager->GetCurrentCharges(id);
                    if (charges < needed)
                        problems.Add($"{ActionInfo(id).Name} {charges}/{maxCharges} ({left / Math.Max(1, maxCharges - charges):0}s)");
                }
                else if (group.First().Kind == StepKind.Ogcd && !manager->IsActionOffCooldown(ActionType.Action, id))
                {
                    problems.Add($"{ActionInfo(id).Name} {left:0}s");
                }
                else if (group.First().IsGcd && left > 3)
                {
                    // A GCD with its own cooldown (Air Anchor, Gnashing Fang...): more than the GCD itself left.
                    problems.Add($"{ActionInfo(id).Name} {left:0}s");
                }
            }

            if (current.Potion && current.Steps.FirstOrDefault(s => s.Kind == StepKind.Potion) is { } pot)
            {
                var itemId = PotionInfo(pot.Stat).ItemId;
                if (itemId != 0 && manager->IsRecastTimerActive(ActionType.Item, itemId))
                {
                    var left = manager->GetRecastTime(ActionType.Item, itemId) - manager->GetRecastTimeElapsed(ActionType.Item, itemId);
                    problems.Add($"Tincture {left:0}s");
                }
            }
        }

        if (problems.Count == 0)
        {
            Theme.Icon(FontAwesomeIcon.CheckCircle, Theme.Good);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Good, "Ready: cooldowns up, full MP.");
            return;
        }

        Theme.Icon(FontAwesomeIcon.ExclamationTriangle, Theme.Warn);
        ImGui.SameLine();
        ImGui.TextColored(Theme.Warn, "Not ready:");
        ImGui.SameLine();
        ImGui.TextWrapped(string.Join(", ", problems));
    }

    /// <summary>The opener's notes from its guide, folded by default.</summary>
    private static void DrawNotes(OpenerDefinition opener)
    {
        if (opener.Notes.Count == 0)
        {
            Theme.Hint($"Source: {opener.Source}.");
            return;
        }

        if (!ImGui.CollapsingHeader($"Notes ({opener.Notes.Count})###openernotes"))
            return;

        using var wrap = ImRaii.TextWrapPos(ImGui.GetCursorPosX() + Theme.Px(820));
        foreach (var note in opener.Notes)
        {
            ImGui.Bullet();
            ImGui.TextColored(Theme.TextMuted, note);
        }

        Theme.Hint($"Source: {opener.Source}.");
    }

    private OpenerRun NewRun(OpenerDefinition opener) =>
        new(opener, plugin.Configuration.OpenerPrepull, plugin.Configuration.OpenerPotion);

    /// <summary>
    /// The game's /countdown starts a new run and sets its pull to zero; a cancelled countdown resets the run.
    /// </summary>
    private unsafe void FollowGameCountdown(OpenerDefinition opener)
    {
        var agent = AgentCountDownSettingDialog.Instance();
        var active = agent != null && agent->Active && agent->TimeRemaining > 0;
        if (active)
        {
            var remaining = agent->TimeRemaining;
            if (!countdownWasActive)
                run = NewRun(opener);
            if (run is { Finished: false })
                run.SyncPull(remaining);
            countdownLeft = remaining;
        }
        else if (countdownWasActive && countdownLeft > 0.5f)
        {
            run = NewRun(opener); // cancelled
        }

        countdownWasActive = active;
    }

    /// <summary>The boxes: GCDs on the track, oGCDs above, on several rows when the opener is long.</summary>
    private void DrawTimeline(OpenerRun current, bool singleRow = false, float scale = 1f)
    {
        var steps = current.Steps;
        var gcdSize = Theme.Px(44) * scale;
        var ogcdSize = Theme.Px(32) * scale;
        var gap = Theme.Px(6) * scale;
        var labelHeight = ImGui.GetTextLineHeight();
        var maxWidth = singleRow ? float.MaxValue : Math.Max(ImGui.GetContentRegionAvail().X, Theme.Px(200)); // follows the window width
        var rowHeight = labelHeight + Theme.Px(4) + ogcdSize + Theme.Px(6) + gcdSize + Theme.Px(4) + labelHeight + Theme.Px(8);

        var fullWidth = steps.Sum(s => (s.IsGcd ? gcdSize : ogcdSize) + gap) + gap * 3;
        if (!singleRow)
            timelineWidth = fullWidth;

        // Layout: row and x of each box, and of the pull marker.
        var positions = new (int Row, float X)[steps.Count];
        var pullAt = (Row: 0, X: -1f);
        var rowWidths = new List<float> { 0 };
        var x = 0f;
        var row = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            var size = steps[i].IsGcd ? gcdSize : ogcdSize;
            var marker = pullAt.X < 0 && steps[i].Prepull is null;
            var needed = size + (marker ? gap * 2 : 0);
            if (x > 0 && x + needed > maxWidth)
            {
                rowWidths[row] = x;
                rowWidths.Add(0);
                row++;
                x = 0;
            }

            if (marker)
            {
                pullAt = (row, x + gap);
                x += gap * 2;
            }

            positions[i] = (row, x);
            x += size + gap;
        }

        rowWidths[row] = x;
        var width = rowWidths.Max() + gap;
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(width, rowHeight * rowWidths.Count));

        var draw = ImGui.GetWindowDrawList();
        float OgcdY(int r) => origin.Y + r * rowHeight + labelHeight + Theme.Px(4);
        float GcdY(int r) => OgcdY(r) + ogcdSize + Theme.Px(6);

        // GCD track of each row.
        var trackHeight = Theme.Px(10);
        for (var r = 0; r < rowWidths.Count; r++)
        {
            draw.AddRectFilled(new Vector2(origin.X, GcdY(r) + (gcdSize - trackHeight) / 2),
                               new Vector2(origin.X + rowWidths[r], GcdY(r) + (gcdSize + trackHeight) / 2),
                               Theme.U32(Theme.Surface), Theme.Px(3));
        }

        if (pullAt.X >= 0)
        {
            var px = origin.X + pullAt.X;
            draw.AddLine(new Vector2(px, OgcdY(pullAt.Row) - Theme.Px(2)), new Vector2(px, GcdY(pullAt.Row) + gcdSize),
                         Theme.U32(Theme.Gold, 0.8f), Theme.Px(2));
            draw.AddText(new Vector2(px + Theme.Px(4), origin.Y + pullAt.Row * rowHeight), Theme.U32(Theme.Gold), "Pull");
        }

        var next = current.Next;
        var wrongFlash = current.LastWrong is { } w && (DateTime.UtcNow - w.When).TotalSeconds < 0.6 ? w.Step : -1;
        var gcdNumber = 0;
        var mouse = ImGui.GetMousePos();
        var hovered = ImGui.IsWindowHovered();

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var size = step.IsGcd ? gcdSize : ogcdSize;
            var (r, bx) = positions[i];
            var pos = new Vector2(origin.X + bx, step.IsGcd ? GcdY(r) : OgcdY(r));
            var max = pos + new Vector2(size, size);

            var state = current.States[i];
            var (icon, name) = Info(step);

            // Pending: full color. Done or missed: greyed out.
            var tint = state == StepState.Pending ? Vector4.One : new Vector4(0.32f, 0.32f, 0.32f, 0.85f);
            if (icon != 0 && Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(icon)).GetWrapOrDefault() is { } wrap)
                draw.AddImageRounded(wrap.Handle, pos, max, Vector2.Zero, Vector2.One, ImGui.GetColorU32(tint), Theme.Px(5));
            else
                draw.AddRectFilled(pos, max, Theme.U32(Theme.SurfaceActive), Theme.Px(5));

            // Border: gold on the next one (red right after a wrong press), green when done, red when missed.
            var (border, thickness) = state switch
            {
                StepState.Done => (Theme.U32(Theme.Good, 0.8f), Theme.Px(1.5f)),
                StepState.Missed => (Theme.U32(Theme.Bad), Theme.Px(2)),
                _ when i == wrongFlash => (Theme.U32(Theme.Bad), Theme.Px(3)),
                _ when i == next && current.Started => (Theme.U32(Theme.GoldBright), Theme.Px(3)),
                _ when i == next => (Theme.U32(Theme.Gold, 0.7f), Theme.Px(2)),
                _ => (Theme.U32(Theme.Line), Theme.Px(1)),
            };
            draw.AddRect(pos, max, border, Theme.Px(5), ImDrawFlags.None, thickness);

            if (state == StepState.Done)
                DrawMark(draw, pos, size, FontAwesomeIcon.Check, Theme.Good);
            else if (state == StepState.Missed)
                DrawMark(draw, pos, size, FontAwesomeIcon.Times, Theme.Bad);

            // Above a pre-pull step: its planned time. Below a GCD: its number, then the time it was used.
            if (step.Prepull is { } prepull)
                draw.AddText(new Vector2(pos.X, origin.Y + r * rowHeight), Theme.U32(Theme.TextMuted), $"-{prepull:0.##}");

            if (step.IsGcd && step.Prepull is null)
            {
                gcdNumber++;
                var label = current.Times[i] is { } t ? $"{t:0.0}" : gcdNumber.ToString();
                var labelWidth = ImGui.CalcTextSize(label).X;
                draw.AddText(new Vector2(pos.X + (size - labelWidth) / 2, GcdY(r) + gcdSize + Theme.Px(4)),
                             Theme.U32(current.Times[i] is null ? Theme.TextFaint : Theme.TextMuted), label);
            }

            if (hovered && mouse.X >= pos.X && mouse.Y >= pos.Y && mouse.X < max.X && mouse.Y < max.Y)
            {
                var kind = step.Kind switch { StepKind.Gcd => "GCD", StepKind.Ogcd => "oGCD", _ => "Tincture (any item counts)" };
                var others = step.Names.Count > 1
                    ? "\nOr: " + string.Join(", ", step.Names.Skip(1).Select(DisplayName))
                    : string.Empty;
                var planned = step.Prepull is { } p ? $", -{p:0.##} s" : string.Empty;
                var used = current.Times[i] is { } u ? $"\nUsed at {u:0.0} s" : string.Empty;
                var missed = state == StepState.Missed ? "\nMissed" : string.Empty;
                ImGui.SetTooltip($"{name}{others}\n{kind}{planned}{used}{missed}");
            }
        }
    }

    private static void DrawMark(ImDrawListPtr draw, Vector2 pos, float size, FontAwesomeIcon icon, Vector4 color)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        var text = icon.ToIconString();
        var textSize = ImGui.CalcTextSize(text);
        draw.AddText(pos + new Vector2(size - textSize.X - Theme.Px(3), size - textSize.Y - Theme.Px(2)), Theme.U32(color), text);
    }

    private void DrawStatus(OpenerRun current)
    {
        var clock = current.Clock;
        var clockText = current.Started ? clock < 0 ? $"{clock:0.0}" : $"{clock:0.0} s" : "--";
        ImGui.TextColored(current.Finished ? Theme.Good : clock < 0 ? Theme.Warn : Theme.Crystal, clockText);
        ImGui.SameLine(0, Theme.Px(16));

        if (current.Finished)
        {
            var clean = current.Mistakes == 0 && current.Missed == 0;
            ImGui.TextColored(clean ? Theme.Good : Theme.Text,
                              $"Done in {current.FinishedAt:0.0} s. Mistakes: {current.Mistakes}, missed: {current.Missed}."
                              + (clean ? " Clean!" : string.Empty));
            return;
        }

        var next = current.Next;
        if (next >= 0)
        {
            var step = current.Steps[next];
            ImGui.TextColored(Theme.TextMuted, "Next:");
            ImGui.SameLine();
            ImGui.TextColored(Theme.GoldBright, Info(step).Name);
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextFaint, step.IsGcd ? "(GCD)" : "(oGCD)");
        }

        if (current.LastWrong is { } wrong)
        {
            ImGui.SameLine(0, Theme.Px(16));
            ImGui.TextColored(Theme.Bad, $"Wrong: {(wrong.Type == 2 ? "item" : ActionInfo(wrong.Id).Name)}");
        }

        if (current.Mistakes > 0 || current.Missed > 0)
        {
            ImGui.SameLine(0, Theme.Px(16));
            ImGui.TextColored(Theme.TextMuted, $"Mistakes: {current.Mistakes}, missed: {current.Missed}");
        }

        if (!current.Started)
            Theme.Hint("Press Start for a countdown, or use the first action to start right away. Big boxes: GCDs, small ones: oGCDs.");
    }

    /// <summary>Icon and name (in the game's language) of a step: its first action, or the tincture.</summary>
    private (uint Icon, string Name) Info(OpenerStep step)
    {
        if (step.Kind == StepKind.Potion)
        {
            var potionInfo = PotionInfo(step.Stat);
            return (potionInfo.Icon, potionInfo.Name);
        }

        var id = ActionNames.Find(step.Names[0]);
        return id != 0 ? ActionInfo(id) : (0u, step.Names[0]);
    }

    private string DisplayName(string englishName)
    {
        var id = ActionNames.Find(englishName);
        return id != 0 ? ActionInfo(id).Name : englishName;
    }

    private (uint Icon, string Name) ActionInfo(uint id)
    {
        if (actions.TryGetValue(id, out var info))
            return info;

        info = Plugin.DataManager.GetExcelSheet<Action>().TryGetRow(id, out var row)
            ? (row.Icon, row.Name.ExtractText())
            : (0u, $"Action {id}");
        actions[id] = info;
        return info;
    }

    /// <summary>The best Gemdraught of the stat (item, icon, name), shown for the tincture step.</summary>
    private (uint ItemId, uint Icon, string Name) PotionInfo(string? stat)
    {
        stat ??= "Strength";
        if (potions.TryGetValue(stat, out var cached))
            return cached;

        var best = Plugin.DataManager.GetExcelSheet<Item>(ClientLanguage.English)
                         .Where(i => i.Name.ExtractText().Contains($"Gemdraught of {stat}", StringComparison.OrdinalIgnoreCase))
                         .Select(i => i.RowId)
                         .DefaultIfEmpty(0u)
                         .Max();
        (uint, uint, string) result = best != 0 && Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(best, out var item)
            ? (best, item.Icon, item.Name.ExtractText())
            : (0u, 0u, "Tincture");
        potions[stat] = result;
        return result;
    }

    private static string CurrentJob()
    {
        var id = Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
        return id != 0 && Plugin.DataManager.GetExcelSheet<ClassJob>(ClientLanguage.English).TryGetRow(id, out var row)
            ? row.Abbreviation.ExtractText()
            : string.Empty;
    }
}
