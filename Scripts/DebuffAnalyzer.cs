using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;

namespace CardReminder.Scripts;

public enum DebuffCategory
{
    Other = 0,
    Doom = 1,
    Poison = 2,
    Weak = 3,
    Vulnerable = 4
}

public sealed record DebuffInfo(
    string PowerId,
    string DisplayName,
    Texture2D? Icon,
    Color HighlightColor,
    DebuffCategory Category,
    PowerModel? CanonicalPower);

public sealed record CardDebuffAnalysis(
    IReadOnlyList<DebuffInfo> Debuffs,
    Color? PrimaryHighlightColor)
{
    public static readonly CardDebuffAnalysis None = new(Array.Empty<DebuffInfo>(), null);

    public bool HasEnemyDebuff => Debuffs.Count > 0;
}

public sealed record TeammateDebuffSummary(
    Player Player,
    string PlayerName,
    IReadOnlyList<DebuffInfo> Debuffs,
    IReadOnlyList<CardModel> DebuffCards);

internal static class DebuffAnalyzer
{
    // 淡红色：易伤高亮外框
    public static readonly Color VulnerableColor = new(1.0f, 0.50f, 0.50f, 0.96f);
    // 淡绿色：虚弱高亮外框
    public static readonly Color WeakColor = new(0.54f, 0.96f, 0.64f, 0.96f);
    // 翠绿色：中毒高亮外框
    public static readonly Color PoisonColor = new(0.32f, 0.90f, 0.48f, 0.96f);
    // 淡紫色：灾厄高亮外框
    public static readonly Color DoomColor = new(0.80f, 0.58f, 1.0f, 0.96f);
    // 淡橙色：其他敌方减益高亮外框
    public static readonly Color OtherDebuffColor = new(1.0f, 0.74f, 0.44f, 0.96f);

    private static Dictionary<string, PowerModel>? _powersById;
    private static Dictionary<Type, PowerModel>? _powersByType;

    public static CardDebuffAnalysis AnalyzeCard(CardModel? card)
    {
        if (card == null)
        {
            return CardDebuffAnalysis.None;
        }

        if (!TargetsEnemy(card.TargetType))
        {
            return CardDebuffAnalysis.None;
        }

        EnsurePowerCache();

        var found = new Dictionary<string, DebuffInfo>(StringComparer.OrdinalIgnoreCase);

        // 1. Check HoverTips (captures Shockwave, Bash, Neutralize, Terror, Trip, Blind, modded cards, etc.)
        try
        {
            foreach (IHoverTip tip in card.HoverTips)
            {
                if (tip is not HoverTip hoverTip || !hoverTip.IsDebuff)
                {
                    continue;
                }

                PowerModel? power = ResolvePowerById(hoverTip.Id);
                if (power != null)
                {
                    AddDebuff(found, power, hoverTip.Icon, hoverTip.Title);
                }
                else if (!string.IsNullOrWhiteSpace(hoverTip.Title))
                {
                    string key = string.IsNullOrWhiteSpace(hoverTip.Id) ? hoverTip.Title : hoverTip.Id;
                    if (!found.ContainsKey(key))
                    {
                        found[key] = new DebuffInfo(
                            PowerId: key,
                            DisplayName: hoverTip.Title,
                            Icon: hoverTip.Icon,
                            HighlightColor: OtherDebuffColor,
                            Category: DebuffCategory.Other,
                            CanonicalPower: null);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DebuffAnalyzer] HoverTips inspection failed for {card.Id}: {ex.Message}");
        }

        // 2. Check DynamicVars for PowerVar<T> (including negative StrengthPower like Disarm / Piercing Wail)
        try
        {
            foreach (DynamicVar dynVar in card.DynamicVars.Values)
            {
                Type varType = dynVar.GetType();
                if (!TryGetPowerVarType(varType, out Type? powerType) || powerType == null)
                {
                    continue;
                }

                PowerModel? power = ResolvePowerByType(powerType);
                if (power == null)
                {
                    continue;
                }

                bool isDebuff = power.Type == PowerType.Debuff
                    || (power is StrengthPower && dynVar.BaseValue < 0m)
                    || (power.AllowNegative && dynVar.BaseValue < 0m);

                if (isDebuff)
                {
                    AddDebuff(found, power, null, null);
                }
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DebuffAnalyzer] DynamicVars inspection failed for {card.Id}: {ex.Message}");
        }

        if (found.Count == 0)
        {
            return CardDebuffAnalysis.None;
        }

        List<DebuffInfo> ordered = found.Values
            .OrderByDescending(static d => (int)d.Category)
            .ThenBy(static d => d.DisplayName, StringComparer.Ordinal)
            .ToList();

        return new CardDebuffAnalysis(ordered, ordered[0].HighlightColor);
    }

    public static List<TeammateDebuffSummary> CollectTeammateDebuffs(
        ICombatState combatState,
        Player localPlayer,
        Creature? targetEnemy,
        bool onlyPlayable,
        out string signature)
    {
        var results = new List<TeammateDebuffSummary>();
        var sigParts = new List<string>();

        List<Creature> enemyScope = targetEnemy is { IsEnemy: true, IsAlive: true }
            ? [targetEnemy]
            : combatState.HittableEnemies.Where(static e => e.IsAlive).ToList();

        foreach (Player teammate in combatState.Players)
        {
            if (teammate == localPlayer || !teammate.Creature.IsAlive)
            {
                continue;
            }

            if (onlyPlayable && CombatManager.Instance.IsPlayerReadyToEndTurn(teammate))
            {
                continue;
            }

            PlayerCombatState? pcs = teammate.PlayerCombatState;
            if (pcs == null || pcs.Hand.IsEmpty)
            {
                continue;
            }

            var teammateDebuffs = new Dictionary<string, DebuffInfo>(StringComparer.OrdinalIgnoreCase);
            var debuffCards = new List<CardModel>();

            foreach (CardModel card in pcs.Hand.Cards)
            {
                if (onlyPlayable && !IsCardLikelyPlayableByTeammate(pcs, card))
                {
                    continue;
                }

                CardDebuffAnalysis analysis = AnalyzeCard(card);
                if (!analysis.HasEnemyDebuff)
                {
                    continue;
                }

                bool cardAdded = false;
                foreach (DebuffInfo debuff in analysis.Debuffs)
                {
                    // For duration debuffs (Vulnerable/Weak), skip only if every enemy in scope already has it.
                    if (ShouldSkipBecauseAlreadyApplied(debuff, enemyScope))
                    {
                        continue;
                    }

                    if (!teammateDebuffs.ContainsKey(debuff.PowerId))
                    {
                        teammateDebuffs[debuff.PowerId] = debuff;
                    }

                    if (!cardAdded)
                    {
                        debuffCards.Add(card);
                        cardAdded = true;
                    }
                }
            }

            if (teammateDebuffs.Count == 0)
            {
                continue;
            }

            List<DebuffInfo> ordered = teammateDebuffs.Values
                .OrderByDescending(static d => (int)d.Category)
                .ThenBy(static d => d.DisplayName, StringComparer.Ordinal)
                .ToList();

            string name = GetPlayerDisplayName(teammate);
            results.Add(new TeammateDebuffSummary(teammate, name, ordered, debuffCards));
            sigParts.Add($"{teammate.NetId}:{string.Join(",", ordered.Select(static d => d.PowerId))}");
        }

        signature = string.Join("|", sigParts);
        return results;
    }

    public static string GetPlayerDisplayName(Player player)
    {
        try
        {
            var netService = RunManager.Instance?.NetService;
            if (netService != null)
            {
                string raw = PlatformUtil.GetPlayerNameRaw(netService.Platform, player.NetId);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return raw;
                }
            }
        }
        catch
        {
            // Fall back to NetId below.
        }

        return player.NetId.ToString();
    }

    private static bool TargetsEnemy(TargetType targetType)
    {
        return targetType is TargetType.AnyEnemy or TargetType.AllEnemies or TargetType.RandomEnemy;
    }

    private static bool IsCardLikelyPlayableByTeammate(PlayerCombatState pcs, CardModel card)
    {
        if (card.Keywords.Contains(CardKeyword.Unplayable))
        {
            return false;
        }

        return pcs.HasEnoughResourcesFor(card, out _);
    }

    private static bool ShouldSkipBecauseAlreadyApplied(DebuffInfo debuff, List<Creature> enemies)
    {
        if (enemies.Count == 0 || debuff.CanonicalPower == null)
        {
            return false;
        }

        // Stackable damage/stat debuffs (Poison, Doom, Strength down) are always worth reminding.
        if (debuff.Category is not (DebuffCategory.Vulnerable or DebuffCategory.Weak))
        {
            return false;
        }

        // If at least one enemy in scope does NOT have this debuff yet, remind!
        return enemies.All(e => e.Powers.Any(p => p.Id == debuff.CanonicalPower.Id && p.Amount > 0));
    }

    private static void AddDebuff(
        Dictionary<string, DebuffInfo> found,
        PowerModel power,
        Texture2D? fallbackIcon,
        string? fallbackTitle)
    {
        string key = power.Id.ToString();
        if (found.ContainsKey(key))
        {
            return;
        }

        Texture2D? icon = null;
        try
        {
            icon = power.Icon ?? fallbackIcon;
        }
        catch
        {
            icon = fallbackIcon;
        }

        string title;
        try
        {
            title = power.Title.GetFormattedText();
            if (string.IsNullOrWhiteSpace(title))
            {
                title = fallbackTitle ?? power.Id.Entry;
            }
        }
        catch
        {
            title = fallbackTitle ?? power.Id.Entry;
        }

        (DebuffCategory category, Color color) = ClassifyPower(power);
        found[key] = new DebuffInfo(key, title, icon, color, category, power);
    }

    private static (DebuffCategory Category, Color Color) ClassifyPower(PowerModel power)
    {
        return power switch
        {
            VulnerablePower => (DebuffCategory.Vulnerable, VulnerableColor),
            WeakPower => (DebuffCategory.Weak, WeakColor),
            PoisonPower => (DebuffCategory.Poison, PoisonColor),
            DoomPower => (DebuffCategory.Doom, DoomColor),
            _ => (DebuffCategory.Other, OtherDebuffColor)
        };
    }

    private static bool TryGetPowerVarType(Type varType, out Type? powerType)
    {
        for (Type? current = varType; current != null && current != typeof(object); current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(PowerVar<>))
            {
                powerType = current.GetGenericArguments()[0];
                return true;
            }
        }

        powerType = null;
        return false;
    }

    private static void EnsurePowerCache()
    {
        if (_powersById != null && _powersByType != null)
        {
            return;
        }

        var byId = new Dictionary<string, PowerModel>(StringComparer.OrdinalIgnoreCase);
        var byType = new Dictionary<Type, PowerModel>();

        foreach (PowerModel power in ModelDb.AllPowers)
        {
            byId[power.Id.ToString()] = power;
            byType[power.GetType()] = power;
        }

        _powersById = byId;
        _powersByType = byType;
    }

    private static PowerModel? ResolvePowerById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || _powersById == null)
        {
            return null;
        }

        return _powersById.TryGetValue(id, out PowerModel? power) ? power : null;
    }

    private static PowerModel? ResolvePowerByType(Type type)
    {
        if (_powersByType == null)
        {
            return null;
        }

        return _powersByType.TryGetValue(type, out PowerModel? power) ? power : null;
    }
}
