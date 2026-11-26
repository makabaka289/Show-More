using System;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using STS2RitsuLib;
using STS2RitsuLib.Patching.Core;
using STS2RitsuLib.Scaffolding.Godot.NodeAttachments;

namespace CardReminder.Scripts;

[ModInitializer(nameof(Init))]
public static class Entry
{
    public const string ModId = "CardReminder";

    public static readonly Logger Logger = RitsuLibFramework.CreateLogger(ModId);

    public static void Init()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        RitsuLibFramework.EnsureGodotScriptsRegistered(assembly, Logger);

        CardReminderSettings.Register();

        ModNodeAttachmentRegistry.For(ModId)
            .RegisterReadyChild<NMultiplayerPlayerState, TeammatePlayerStateAttachment>(
                TeammatePlayerStateAttachment.AttachmentId,
                static _ => new TeammatePlayerStateAttachment(),
                static (parent, attachment) => attachment.Bind(parent),
                new NodeAttachmentOptions
                {
                    Name = TeammatePlayerStateAttachment.AttachmentNodeName,
                    Order = 20,
                    DuplicatePolicy = NodeAttachmentDuplicatePolicy.ReuseExistingByName,
                    SetupTiming = NodeAttachmentSetupTiming.AfterAdd
                });

        var patcher = RitsuLibFramework.CreatePatcher(ModId, "core-patches");
        patcher.RegisterPatch<CardPlayReminderPatch>();
        patcher.RegisterPatch<PlayerStateHitboxPatch>();
        patcher.RegisterPatch<SuppressVanillaPotionObtainedAnimPatch>();
        if (!patcher.PatchAll())
        {
            Logger.Warn("Some non-critical patches failed to apply.");
        }

        RitsuLibFramework.SubscribeLifecycle<CombatStartingEvent>(static _ => ReminderTurnState.Reset());
        RitsuLibFramework.SubscribeLifecycle<CombatEndedEvent>(static _ => ReminderTurnState.Reset());
        RitsuLibFramework.SubscribeLifecycle<RunEndedEvent>(static _ => ReminderTurnState.Reset());
        RitsuLibFramework.SubscribeLifecycle<SideTurnStartedEvent>(static evt =>
        {
            if (evt.Side == CombatSide.Player)
            {
                ReminderTurnState.Reset();
            }
        });

        Logger.Info("CardReminder initialized with RitsuLib.");
    }
}
