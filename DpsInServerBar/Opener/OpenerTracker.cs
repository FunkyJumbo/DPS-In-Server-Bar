using System;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace DpsInServerBar.Opener;

/// <summary>
/// Reports the actions you use (as confirmed by the server), only while enabled, i.e. while the opener practice
/// window is open. Independent from the combat tracker, which may be turned off.
/// </summary>
public sealed unsafe class OpenerTracker : IDisposable
{
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly Hook<ActionEffectHandler.Delegates.Receive>? hook;

    public OpenerTracker(IGameInteropProvider interop, IObjectTable objectTable, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.log = log;
        try
        {
            hook = interop.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
                ActionEffectHandler.Addresses.Receive.Value, OnActionEffect);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to create the opener practice hook");
        }
    }

    /// <summary>(action type: 1 = action, 2 = item; action or item id). Raised on the game thread.</summary>
    public event Action<byte, uint>? ActionUsed;

    public bool Available => hook is not null;

    public bool Enabled
    {
        get => hook?.IsEnabled == true;
        set
        {
            if (hook is null || hook.IsEnabled == value)
                return;
            if (value)
                hook.Enable();
            else
                hook.Disable();
        }
    }

    public void Dispose() => hook?.Dispose();

    private void OnActionEffect(
        uint casterEntityId, Character* casterPtr, Vector3* targetPos,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        hook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);

        try
        {
            if (casterEntityId == 0 || casterEntityId != objectTable.LocalPlayer?.EntityId)
                return;

            var type = (byte)header->ActionType;
            var id = header->ActionId;
            if (type is 1 or 2 && id is not (7 or 8)) // 7 / 8: auto-attacks
                ActionUsed?.Invoke(type, id);
        }
        catch (Exception ex)
        {
            log.Warning($"Opener practice: {ex.Message}");
        }
    }
}
