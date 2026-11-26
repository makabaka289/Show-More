using System;
using STS2RitsuLib;
using STS2RitsuLib.Data;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;

namespace CardReminder.Scripts;

public sealed class CardReminderConfig
{
    public bool ShowGoldAndPotions { get; set; } = true;
    public bool ShowTeammateHandCards { get; set; } = true;
    public bool ShowOwnHandCards { get; set; } = false;
    public int CardScalePercent { get; set; } = 26;
    public bool HoverZoomEnabled { get; set; } = true;
    public bool EnablePlayCardReminder { get; set; } = true;
    public bool InterceptFirstPlayOnReminder { get; set; } = true;
    public bool ReminderOnlyPlayableDebuffs { get; set; } = true;
    public string ReminderTriggerScope { get; set; } = "all";
}

public static class CardReminderSettings
{
    private const string DataKey = "settings";

    public static event Action? SettingsChanged;

    private static readonly ModSettingsValueBinding<CardReminderConfig, bool> ShowGoldAndPotionsBinding = new(
        Entry.ModId, DataKey, SaveScope.Global,
        static s => s.ShowGoldAndPotions,
        static (s, v) =>
        {
            s.ShowGoldAndPotions = v;
            NotifyChanged();
        });

    private static readonly ModSettingsValueBinding<CardReminderConfig, bool> ShowTeammateHandCardsBinding = new(
        Entry.ModId, DataKey, SaveScope.Global,
        static s => s.ShowTeammateHandCards,
        static (s, v) =>
        {
            s.ShowTeammateHandCards = v;
            NotifyChanged();
        });

    private static readonly ModSettingsValueBinding<CardReminderConfig, bool> ShowOwnHandCardsBinding = new(
        Entry.ModId, DataKey, SaveScope.Global,
        static s => s.ShowOwnHandCards,
        static (s, v) =>
        {
            s.ShowOwnHandCards = v;
            NotifyChanged();
        });

    private static readonly ModSettingsValueBinding<CardReminderConfig, int> CardScalePercentBinding = new(
        Entry.ModId, DataKey, SaveScope.Global,
        static s => s.CardScalePercent,
        static (s, v) =>
        {
            s.CardScalePercent = Math.Clamp(v, 18, 42);
            NotifyChanged();
        });

    private static readonly ModSettingsValueBinding<CardReminderConfig, bool> HoverZoomEnabledBinding = new(
        Entry.ModId, DataKey, SaveScope.Global,
        static s => s.HoverZoomEnabled,
        static (s, v) =>
        {
            s.HoverZoomEnabled = v;
            NotifyChanged();
        });

    private static readonly ModSettingsValueBinding<CardReminderConfig, bool> EnablePlayCardReminderBinding = new(
        Entry.ModId, DataKey, SaveScope.Global,
        static s => s.EnablePlayCardReminder,
        static (s, v) =>
        {
            s.EnablePlayCardReminder = v;
            NotifyChanged();
        });

    private static readonly ModSettingsValueBinding<CardReminderConfig, bool> InterceptFirstPlayBinding = new(
        Entry.ModId, DataKey, SaveScope.Global,
        static s => s.InterceptFirstPlayOnReminder,
        static (s, v) =>
        {
            s.InterceptFirstPlayOnReminder = v;
            NotifyChanged();
        });

    private static readonly ModSettingsValueBinding<CardReminderConfig, bool> ReminderOnlyPlayableBinding = new(
        Entry.ModId, DataKey, SaveScope.Global,
        static s => s.ReminderOnlyPlayableDebuffs,
        static (s, v) =>
        {
            s.ReminderOnlyPlayableDebuffs = v;
            NotifyChanged();
        });

    private static readonly ModSettingsValueBinding<CardReminderConfig, string> ReminderTriggerScopeBinding = new(
        Entry.ModId, DataKey, SaveScope.Global,
        static s => s.ReminderTriggerScope,
        static (s, v) =>
        {
            s.ReminderTriggerScope = string.IsNullOrWhiteSpace(v) ? "all" : v;
            NotifyChanged();
        });

    public static bool ShowGoldAndPotions => SafeRead(ShowGoldAndPotionsBinding, true);
    public static bool ShowTeammateHandCards => SafeRead(ShowTeammateHandCardsBinding, true);
    public static bool ShowOwnHandCards => SafeRead(ShowOwnHandCardsBinding, false);
    public static float CardScale => Math.Clamp(SafeRead(CardScalePercentBinding, 26), 18, 42) / 100f;
    public static bool HoverZoomEnabled => SafeRead(HoverZoomEnabledBinding, true);
    public static bool EnablePlayCardReminder => SafeRead(EnablePlayCardReminderBinding, true);
    public static bool InterceptFirstPlayOnReminder => SafeRead(InterceptFirstPlayBinding, true);
    public static bool ReminderOnlyPlayableDebuffs => SafeRead(ReminderOnlyPlayableBinding, true);
    public static bool ReminderAttackCardsOnly => string.Equals(SafeRead(ReminderTriggerScopeBinding, "all"), "attack", StringComparison.OrdinalIgnoreCase);

    public static void Register()
    {
        ModDataStore.For(Entry.ModId).Register<CardReminderConfig>(
            key: DataKey,
            fileName: "settings.json",
            scope: SaveScope.Global,
            defaultFactory: static () => new CardReminderConfig(),
            autoCreateIfMissing: true);

        RitsuLibFramework.RegisterModSettings(Entry.ModId, page => page
            .WithTitle(ModSettingsText.Literal("队友手牌与Debuff提醒"))
            .WithModDisplayName(ModSettingsText.Literal("CardReminder"))
            .WithVisibleOnHostSurfaces(ModSettingsHostSurface.MainMenu | ModSettingsHostSurface.RunPause)
            .AddSection("display", section => section
                .WithTitle(ModSettingsText.Literal("玩家列表显示"))
                .AddToggle("show_gold_potions", ModSettingsText.Literal("队友ID右侧显示金币与药水"), ShowGoldAndPotionsBinding)
                .AddToggle("show_teammate_cards", ModSettingsText.Literal("队友右侧显示手牌"), ShowTeammateHandCardsBinding)
                .AddToggle("show_own_cards", ModSettingsText.Literal("自己右侧也显示手牌"), ShowOwnHandCardsBinding)
                .AddIntSlider("card_scale", ModSettingsText.Literal("手牌基础缩放比例"), CardScalePercentBinding,
                    minValue: 18, maxValue: 42, step: 2,
                    valueFormatter: static v => $"{v}%")
                .AddToggle("hover_zoom", ModSettingsText.Literal("悬浮时原位放大 3 倍"), HoverZoomEnabledBinding))
            .AddSection("reminder", section => section
                .WithTitle(ModSettingsText.Literal("出牌 Debuff 提醒"))
                .AddToggle("enable_reminder", ModSettingsText.Literal("出牌时提醒队友持有 Debuff 卡牌"), EnablePlayCardReminderBinding)
                .AddToggle("intercept_first_play", ModSettingsText.Literal("提醒时暂停首次出牌（防误出）"), InterceptFirstPlayBinding)
                .AddToggle("only_playable_debuffs", ModSettingsText.Literal("仅提醒队友当前能量可打出的 Debuff 牌"), ReminderOnlyPlayableBinding)
                .AddChoice("trigger_scope", ModSettingsText.Literal("触发提醒的卡牌类型"), ReminderTriggerScopeBinding,
                    [
                        new("all", ModSettingsText.Literal("任意卡牌")),
                        new("attack", ModSettingsText.Literal("仅攻击牌"))
                    ],
                    presentation: ModSettingsChoicePresentation.Dropdown)));
    }

    private static T SafeRead<T>(ModSettingsValueBinding<CardReminderConfig, T> binding, T fallback)
    {
        try
        {
            return binding.Read();
        }
        catch
        {
            return fallback;
        }
    }

    private static void NotifyChanged()
    {
        SettingsChanged?.Invoke();
    }
}
