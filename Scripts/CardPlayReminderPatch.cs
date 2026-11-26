using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using STS2RitsuLib.Patching.Models;
using STS2RitsuLib.Scaffolding.Godot.NodeAttachments;

namespace CardReminder.Scripts;

/// <summary>
/// Tracks per-turn reminder deduplication and handles reminder evaluation when the local player plays a card.
/// </summary>
internal static class ReminderTurnState
{
    private static int _lastRemindedRound = -1;
    private static string _lastRemindedSignature = string.Empty;

    public static void Reset()
    {
        _lastRemindedRound = -1;
        _lastRemindedSignature = string.Empty;
        PlayCardDebuffReminder.DismissActive();
    }

    public static bool ShouldRemind(int roundNumber, string signature)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return false;
        }

        return _lastRemindedRound != roundNumber || !string.Equals(_lastRemindedSignature, signature, StringComparison.Ordinal);
    }

    public static void MarkReminded(int roundNumber, string signature)
    {
        _lastRemindedRound = roundNumber;
        _lastRemindedSignature = signature;
    }
}

/// <summary>
/// RitsuLib patch on CardModel.TryManualPlay to show the teammate debuff reminder
/// when the local player plays a card while teammates hold Vulnerable/Weak/etc.
/// </summary>
public sealed class CardPlayReminderPatch : IPatchMethod
{
    public static string PatchId => "card_reminder_try_manual_play";

    public static string Description => "Show teammate debuff reminder when local player plays a card";

    public static bool IsCritical => false;

    public static ModPatchTarget[] GetTargets() =>
        [new(typeof(CardModel), nameof(CardModel.TryManualPlay))];

    public static bool Prefix(CardModel __instance, Creature? target, ref bool __result)
    {
        try
        {
            if (!CardReminderSettings.EnablePlayCardReminder)
            {
                return true;
            }

            if (!CombatManager.Instance.IsInProgress || !LocalContext.IsMine(__instance))
            {
                return true;
            }

            if (CardReminderSettings.ReminderAttackCardsOnly && __instance.Type != CardType.Attack)
            {
                return true;
            }

            if (__instance.Owner is not { } owner)
            {
                return true;
            }

            ICombatState? combatState = __instance.CombatState ?? owner.Creature?.CombatState;
            if (combatState == null || combatState.Players.Count <= 1)
            {
                return true;
            }

            if (!__instance.CanPlayTargeting(target))
            {
                return true;
            }

            List<TeammateDebuffSummary> teammates = DebuffAnalyzer.CollectTeammateDebuffs(
                combatState,
                owner,
                target,
                CardReminderSettings.ReminderOnlyPlayableDebuffs,
                out string signature);

            if (teammates.Count == 0 || !ReminderTurnState.ShouldRemind(combatState.RoundNumber, signature))
            {
                return true;
            }

            ReminderTurnState.MarkReminded(combatState.RoundNumber, signature);

            bool intercept = CardReminderSettings.InterceptFirstPlayOnReminder;
            PlayCardDebuffReminder.Show(teammates, intercept);

            if (intercept)
            {
                __result = false;
                return false;
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Error($"[CardPlayReminderPatch] Error checking teammate debuffs: {ex}");
        }

        return true;
    }
}

/// <summary>
/// Keeps NMultiplayerPlayerState.Hitbox height aligned to the header row so the expanded
/// teammate hand card tray below Player ID receives hover events cleanly.
/// </summary>
public sealed class PlayerStateHitboxPatch : IPatchMethod
{
    public static string PatchId => "card_reminder_player_state_hitbox";

    public static string Description => "Clamp NMultiplayerPlayerState Hitbox height above teammate card tray";

    public static bool IsCritical => false;

    public static ModPatchTarget[] GetTargets() =>
        [new(typeof(NMultiplayerPlayerState), "UpdateSelectionReticleWidth", ignoreIfMissing: true)];

    public static void Postfix(NMultiplayerPlayerState __instance)
    {
        if (ModNodeAttachmentRegistry.For(Entry.ModId).TryGetAttached<NMultiplayerPlayerState, TeammatePlayerStateAttachment>(
                __instance,
                TeammatePlayerStateAttachment.AttachmentId,
                out var attachment))
        {
            attachment.ClampHitboxToHeader(__instance);
        }
    }
}

/// <summary>
/// Suppresses the vanilla NMultiplayerPlayerState potion-obtained floating animation
/// so only our smooth 1.0s potion drop-in transition plays next to teammate Player IDs.
/// </summary>
public sealed class SuppressVanillaPotionObtainedAnimPatch : IPatchMethod
{
    public static string PatchId => "card_reminder_suppress_vanilla_potion_anim";

    public static string Description => "Hide vanilla NMultiplayerPlayerState.OnPotionProcured animation";

    public static bool IsCritical => false;

    public static ModPatchTarget[] GetTargets() =>
        [new(typeof(NMultiplayerPlayerState), "OnPotionProcured", ignoreIfMissing: true)];

    public static bool Prefix()
    {
        return !CardReminderSettings.ShowGoldAndPotions;
    }
}
