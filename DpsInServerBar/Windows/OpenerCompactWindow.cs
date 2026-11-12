using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DpsInServerBar.UI;

namespace DpsInServerBar.Windows;

/// <summary>
/// Compact opener practice: a small pop-up without a title bar, sized to its content, with only the opener's boxes,
/// Start / Reset and the way back to the full window. Dragged by its background, right click for a menu. The try
/// itself lives in the full window, so switching view keeps it.
/// </summary>
public sealed class OpenerCompactWindow : Window
{
    private readonly Plugin plugin;
    private readonly OpenerWindow full;
    private IDisposable? themeScope;
    private IDisposable? styleScope;

    public OpenerCompactWindow(Plugin plugin, OpenerWindow full)
        : base(Loc.T("Opener practice (compact)###DpsInServerBarOpenerCompact"),
               ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
               | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize)
    {
        Loc.Changed += () => WindowName = Loc.T("Opener practice (compact)###DpsInServerBarOpenerCompact");
        this.plugin = plugin;
        this.full = full;
        RespectCloseHotkey = false;
        AllowBackgroundBlur = false;
    }

    public override void OnOpen() => plugin.UpdateOpenerTracking();

    public override void OnClose() => plugin.UpdateOpenerTracking();

    public override void PreDraw()
    {
        BgAlpha = plugin.Configuration.OpenerOpacity; // the background only, not the icons
        themeScope = Theme.Push();
        var scale = Scale;
        styleScope = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, Theme.Px(10) * scale)
                          .Push(ImGuiStyleVar.WindowPadding, Theme.Px(new Vector2(8, 7)) * scale)
                          .Push(ImGuiStyleVar.FramePadding, ImGui.GetStyle().FramePadding * scale)
                          .Push(ImGuiStyleVar.ItemSpacing, ImGui.GetStyle().ItemSpacing * scale);
    }

    public override void PostDraw()
    {
        styleScope?.Dispose();
        styleScope = null;
        themeScope?.Dispose();
        themeScope = null;
    }

    public override void Draw()
    {
        ImGui.SetWindowFontScale(Scale);
        DrawMenu();
        full.DrawCompact();
    }

    private float Scale => Math.Clamp(plugin.Configuration.OpenerCompactScale, 0.5f, 1.5f);

    /// <summary>Right click menu: size, background opacity, full view, close.</summary>
    private void DrawMenu()
    {
        var config = plugin.Configuration;
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows) && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("##openercompactmenu");

        using var popup = ImRaii.Popup("##openercompactmenu");
        if (!popup)
            return;

        ImGui.SetWindowFontScale(1f);
        var size = (int)Math.Round(config.OpenerCompactScale * 100);
        ImGui.SetNextItemWidth(Theme.Px(140));
        if (ImGui.SliderInt(Loc.T("Size"), ref size, 50, 150, "%d%%"))
            config.OpenerCompactScale = size / 100f;
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();

        var percent = (int)Math.Round(config.OpenerOpacity * 100);
        ImGui.SetNextItemWidth(Theme.Px(140));
        if (ImGui.SliderInt(Loc.T("Background"), ref percent, 0, 100, "%d%%"))
            config.OpenerOpacity = percent / 100f;
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();

        ImGui.Separator();
        if (ImGui.MenuItem(Loc.T("Full view")))
            plugin.SetOpenerCompact(false);
        if (ImGui.MenuItem(Loc.T("Close")))
            plugin.CloseOpener();
    }
}
