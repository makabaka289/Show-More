using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace CardReminder.Scripts;

/// <summary>
/// Renders a player's current combat hand cards in exact hand order on a single horizontal row
/// to the right of the player entry (never wrapping even when holding > 5 cards),
/// with smooth sequential left-to-right enter transitions and smooth exit transitions.
/// </summary>
public sealed partial class TeammateCardTray : Control
{
    private const float DefaultHorizontalGap = 6f;
    private const float CompactHorizontalGap = 3f;
    private const double SequentialStaggerSeconds = 0.075;

    private HBoxContainer _row = null!;
    private readonly List<TeammateCardSlot> _activeSlots = [];
    private readonly Dictionary<CardModel, TeammateCardSlot> _slotsByModel = new(ReferenceEqualityComparer.Instance);
    private int _hoveredSlotCount;
    private Tween? _trayFadeTween;
    private double _nextEnterScheduleSec;

    public event Action<float>? TrayHeightChanged;
    public event Action<bool>? AnyCardHoverChanged;

    public float CurrentTrayHeight { get; private set; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = false;

        _row = new HBoxContainer
        {
            Name = "CardRow",
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = false
        };
        _row.AddThemeConstantOverride("separation", (int)DefaultHorizontalGap);
        AddChild(_row);
    }

    public override void _ExitTree()
    {
        _trayFadeTween?.Kill();
        ClearSlotsImmediate();
        base._ExitTree();
    }

    public void UpdateCards(Player? player, bool visibleInCombat)
    {
        if (!IsNodeReady())
        {
            return;
        }

        IReadOnlyList<CardModel>? handCards = visibleInCombat
            ? player?.PlayerCombatState?.Hand.Cards
            : null;

        if (!visibleInCombat)
        {
            FadeOutAndHide(clearHeight: true);
            return;
        }

        if (handCards == null || handCards.Count == 0)
        {
            // During turn-end flush, animate cards out smoothly without collapsing combat row height
            AnimateAllCardsOut();
            return;
        }

        _trayFadeTween?.Kill();
        if (!Visible)
        {
            Visible = true;
            Modulate = new Color(1f, 1f, 1f, 0f);
            _trayFadeTween = CreateTween();
            _trayFadeTween.TweenProperty(this, "modulate:a", 1f, 0.18)
                .SetTrans(Tween.TransitionType.Cubic)
                .SetEase(Tween.EaseType.Out);
        }
        else if (Modulate.A < 0.99f)
        {
            _trayFadeTween = CreateTween();
            _trayFadeTween.TweenProperty(this, "modulate:a", 1f, 0.16)
                .SetTrans(Tween.TransitionType.Cubic)
                .SetEase(Tween.EaseType.Out);
        }

        float scale = CardReminderSettings.CardScale;
        Vector2 slotSize = NCard.defaultSize * scale;
        float gap = ComputeSingleRowGap(handCards.Count, slotSize.X);
        _row.AddThemeConstantOverride("separation", Mathf.RoundToInt(gap));

        float rowWidth = handCards.Count * slotSize.X + Math.Max(0, handCards.Count - 1) * gap + 8f;
        float totalHeight = slotSize.Y + 6f;

        _row.CustomMinimumSize = new Vector2(rowWidth, totalHeight);
        _row.Size = new Vector2(rowWidth, totalHeight);
        CustomMinimumSize = new Vector2(rowWidth, totalHeight);
        Size = new Vector2(rowWidth, totalHeight);

        var nextSet = new HashSet<CardModel>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < handCards.Count; i++)
        {
            nextSet.Add(handCards[i]);
        }

        // Smoothly exit cards that left the hand
        for (int i = _activeSlots.Count - 1; i >= 0; i--)
        {
            TeammateCardSlot slot = _activeSlots[i];
            CardModel? model = slot.CardModel;
            if (model == null || !nextSet.Contains(model))
            {
                _activeSlots.RemoveAt(i);
                if (model != null)
                {
                    _slotsByModel.Remove(model);
                }

                if (GodotObject.IsInstanceValid(slot))
                {
                    DetachAndAnimateExit(slot);
                }
            }
        }

        // Reuse existing slots or create new ones in exact teammate hand order (staggered left-to-right)
        double nowSec = Time.GetTicksMsec() / 1000.0;
        var orderedSlots = new List<TeammateCardSlot>(handCards.Count);

        for (int i = 0; i < handCards.Count; i++)
        {
            CardModel card = handCards[i];
            if (_slotsByModel.TryGetValue(card, out TeammateCardSlot? existing) && GodotObject.IsInstanceValid(existing) && !existing.IsExiting)
            {
                existing.SetCard(card, scale);
                orderedSlots.Add(existing);
            }
            else
            {
                var created = new TeammateCardSlot
                {
                    HoverStateChanged = OnSlotHoverChanged
                };
                created.SetCard(card, scale);
                _row.AddChild(created);

                double startSec = Math.Max(nowSec, _nextEnterScheduleSec);
                double delay = Math.Clamp(startSec - nowSec, 0.0, 0.65);
                _nextEnterScheduleSec = startSec + SequentialStaggerSeconds;
                created.PlayEnterAnimation(delay);

                _slotsByModel[card] = created;
                orderedSlots.Add(created);
            }
        }

        _activeSlots.Clear();
        _activeSlots.AddRange(orderedSlots);

        // Enforce exact child ordering matching teammate hand order
        for (int i = 0; i < _activeSlots.Count; i++)
        {
            TeammateCardSlot slot = _activeSlots[i];
            if (GodotObject.IsInstanceValid(slot) && slot.GetParent() == _row)
            {
                _row.MoveChild(slot, i);
            }
        }

        SetTrayHeight(totalHeight);
    }

    public void RefreshCardVisuals()
    {
        if (!Visible || _activeSlots.Count == 0)
        {
            return;
        }

        foreach (TeammateCardSlot slot in _activeSlots)
        {
            if (GodotObject.IsInstanceValid(slot) && !slot.IsExiting)
            {
                slot.RefreshVisualsOnly();
            }
        }
    }

    /// <summary>
    /// Keeps all cards on one single row; when >5 cards, gently tightens gap (or slightly overlaps
    /// only if needed to fit inside the visible viewport width).
    /// </summary>
    private float ComputeSingleRowGap(int cardCount, float slotWidth)
    {
        if (cardCount <= 1)
        {
            return DefaultHorizontalGap;
        }

        float preferredGap = cardCount <= 5 ? DefaultHorizontalGap : CompactHorizontalGap;
        if (!IsInsideTree())
        {
            return preferredGap;
        }

        Rect2 visibleRect = GetCanvasTransform().AffineInverse() * GetViewportRect();
        float availableWidth = visibleRect.End.X - GlobalPosition.X - 24f;
        if (availableWidth <= slotWidth)
        {
            return preferredGap;
        }

        float neededWidth = cardCount * slotWidth + (cardCount - 1) * preferredGap;
        if (neededWidth <= availableWidth)
        {
            return preferredGap;
        }

        float fittedGap = (availableWidth - cardCount * slotWidth) / (cardCount - 1);
        return Math.Max(-slotWidth * 0.45f, fittedGap);
    }

    private void AnimateAllCardsOut()
    {
        _nextEnterScheduleSec = 0.0;
        if (_activeSlots.Count == 0)
        {
            return;
        }

        var toExit = new List<TeammateCardSlot>(_activeSlots);
        _activeSlots.Clear();
        _slotsByModel.Clear();

        foreach (TeammateCardSlot slot in toExit)
        {
            if (GodotObject.IsInstanceValid(slot))
            {
                DetachAndAnimateExit(slot);
            }
        }

        if (_hoveredSlotCount > 0)
        {
            _hoveredSlotCount = 0;
            AnyCardHoverChanged?.Invoke(false);
        }
    }

    private void DetachAndAnimateExit(TeammateCardSlot slot)
    {
        slot.HoverStateChanged = null;
        if (slot.GetParent() == _row)
        {
            Vector2 posInTray = slot.Position + _row.Position;
            _row.RemoveChild(slot);
            AddChild(slot);
            slot.Position = posInTray;
            slot.ZIndex = 0;
        }
        slot.PlayExitAndFree();
    }

    private void FadeOutAndHide(bool clearHeight)
    {
        _trayFadeTween?.Kill();

        if (!Visible && _activeSlots.Count == 0)
        {
            if (clearHeight)
            {
                SetTrayHeight(0f);
            }
            return;
        }

        AnimateAllCardsOut();

        _trayFadeTween = CreateTween();
        _trayFadeTween.TweenProperty(this, "modulate:a", 0f, 0.22)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.Out);
        _trayFadeTween.TweenCallback(Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(this))
            {
                return;
            }
            Visible = false;
            if (clearHeight)
            {
                SetTrayHeight(0f);
            }
        }));
    }

    private void OnSlotHoverChanged(bool hovered)
    {
        bool wasHovered = _hoveredSlotCount > 0;
        _hoveredSlotCount = Math.Max(0, _hoveredSlotCount + (hovered ? 1 : -1));
        bool nowHovered = _hoveredSlotCount > 0;

        if (wasHovered != nowHovered)
        {
            AnyCardHoverChanged?.Invoke(nowHovered);
        }
    }

    private void SetTrayHeight(float height)
    {
        if (Mathf.IsEqualApprox(CurrentTrayHeight, height))
        {
            return;
        }

        CurrentTrayHeight = height;
        TrayHeightChanged?.Invoke(height);
    }

    private void ClearSlotsImmediate()
    {
        bool wasHovered = _hoveredSlotCount > 0;
        _hoveredSlotCount = 0;

        foreach (TeammateCardSlot slot in _activeSlots)
        {
            if (GodotObject.IsInstanceValid(slot))
            {
                slot.HoverStateChanged = null;
                slot.QueueFree();
            }
        }

        _activeSlots.Clear();
        _slotsByModel.Clear();

        if (wasHovered)
        {
            AnyCardHoverChanged?.Invoke(false);
        }
    }
}
