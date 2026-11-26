using System;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace CardReminder.Scripts;

/// <summary>
/// Displays a real scaled NCard with live art/numbers, vanilla playable edge highlight for normal cards,
/// bright debuff outer border for playable debuff cards, darkened face + colored border for unplayable debuff cards,
/// top-right debuff icon badges, smooth sequential transitions, and 3x in-place hover zoom.
/// </summary>
public sealed partial class TeammateCardSlot : Control
{
    private const float HoverZoomFactor = 3.0f;
    private static readonly Color UnplayableDebuffTint = new(0.48f, 0.48f, 0.52f, 1.0f);

    private Control _visualRoot = null!;
    private Panel _highlightFrame = null!;
    private HBoxContainer _debuffBadgeRow = null!;
    private NCard? _cardNode;
    private CardModel? _cardModel;
    private CardDebuffAnalysis _debuffAnalysis = CardDebuffAnalysis.None;
    private float _baseScale = 0.26f;
    private bool _isHovered;
    private bool _isExiting;
    private bool _isPlayable;
    private bool _vanillaHighlightShown;
    private Tween? _zoomTween;
    private Tween? _pulseTween;
    private Tween? _transitionTween;

    public Action<bool>? HoverStateChanged { get; set; }

    public CardModel? CardModel => _cardModel;

    public bool IsExiting => _isExiting;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        ClipContents = false;

        _visualRoot = new Control
        {
            Name = "VisualRoot",
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = false
        };
        AddChild(_visualRoot);

        _highlightFrame = new Panel
        {
            Name = "DebuffHighlightFrame",
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 0
        };
        _visualRoot.AddChild(_highlightFrame);

        _debuffBadgeRow = new HBoxContainer
        {
            Name = "DebuffBadgeRow",
            Visible = false,
            Alignment = BoxContainer.AlignmentMode.End,
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 0
        };
        _debuffBadgeRow.AddThemeConstantOverride("separation", 2);
        _visualRoot.AddChild(_debuffBadgeRow);

        MouseEntered += OnMouseEntered;
        MouseExited += OnMouseExited;

        ApplyScaleLayout();

        if (_cardModel != null)
        {
            AttachOrUpdateCard(_cardModel);
        }
    }

    public override void _ExitTree()
    {
        MouseEntered -= OnMouseEntered;
        MouseExited -= OnMouseExited;
        _zoomTween?.Kill();
        _pulseTween?.Kill();
        _transitionTween?.Kill();
        NHoverTipSet.Remove(this);

        if (_cardNode != null && GodotObject.IsInstanceValid(_cardNode))
        {
            _cardNode.QueueFreeSafely();
        }
        _cardNode = null;

        base._ExitTree();
    }

    public void SetCard(CardModel card, float baseScale)
    {
        bool scaleChanged = !Mathf.IsEqualApprox(_baseScale, baseScale);
        _baseScale = baseScale;
        _cardModel = card;

        if (!IsNodeReady())
        {
            return;
        }

        if (scaleChanged)
        {
            ApplyScaleLayout();
        }

        AttachOrUpdateCard(card);
    }

    /// <summary>
    /// Smoothly reveals the card sliding in from left to right with optional sequential stagger delay.
    /// </summary>
    public void PlayEnterAnimation(double delaySeconds = 0.0)
    {
        if (!IsNodeReady() || _isExiting)
        {
            return;
        }

        Vector2 restPos = GetRestVisualPosition();
        _transitionTween?.Kill();
        _visualRoot.Position = restPos + new Vector2(-20f, 4f);
        _visualRoot.Scale = new Vector2(0.84f, 0.84f);
        _visualRoot.Modulate = new Color(1f, 1f, 1f, 0f);

        _transitionTween = CreateTween().SetParallel();
        _transitionTween.TweenProperty(_visualRoot, "modulate:a", 1f, 0.24)
            .SetDelay(delaySeconds)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);
        _transitionTween.TweenProperty(_visualRoot, "position", restPos, 0.28)
            .SetDelay(delaySeconds)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);
        _transitionTween.TweenProperty(_visualRoot, "scale", Vector2.One, 0.28)
            .SetDelay(delaySeconds)
            .SetTrans(Tween.TransitionType.Back)
            .SetEase(Tween.EaseType.Out);
    }

    public void PlayExitAndFree(Action? onFinished = null)
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        MouseFilter = MouseFilterEnum.Ignore;
        NHoverTipSet.Remove(this);

        if (_isHovered)
        {
            _isHovered = false;
            HoverStateChanged?.Invoke(false);
        }

        if (!IsNodeReady() || !IsInsideTree())
        {
            onFinished?.Invoke();
            QueueFree();
            return;
        }

        _zoomTween?.Kill();
        _pulseTween?.Kill();
        _transitionTween?.Kill();

        Vector2 targetPos = _visualRoot.Position + new Vector2(-10f, 10f);
        _transitionTween = CreateTween().SetParallel();
        _transitionTween.TweenProperty(_visualRoot, "modulate:a", 0f, 0.20)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.In);
        _transitionTween.TweenProperty(_visualRoot, "scale", new Vector2(0.76f, 0.76f), 0.20)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.In);
        _transitionTween.TweenProperty(_visualRoot, "position", targetPos, 0.20)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.In);
        _transitionTween.Chain().TweenCallback(Callable.From(() =>
        {
            onFinished?.Invoke();
            if (GodotObject.IsInstanceValid(this))
            {
                QueueFree();
            }
        }));
    }

    public void RefreshVisualsOnly()
    {
        if (!IsNodeReady() || _isExiting || _cardModel == null)
        {
            return;
        }

        if (_cardNode != null && GodotObject.IsInstanceValid(_cardNode) && _cardNode.IsNodeReady())
        {
            _cardNode.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            SetMouseIgnoreRecursive(_cardNode);
        }

        ApplyCardHighlightAndState(_cardModel);
    }

    private Vector2 GetRestVisualPosition() => NCard.defaultSize * _baseScale * 0.5f;

    private void ApplyScaleLayout()
    {
        Vector2 slotSize = NCard.defaultSize * _baseScale;
        CustomMinimumSize = slotSize;
        Size = slotSize;

        if (!_isHovered)
        {
            _visualRoot.Position = slotSize * 0.5f;
            _visualRoot.Scale = Vector2.One;
        }
        _visualRoot.Size = Vector2.Zero;

        Vector2 framePadding = new(8f, 8f);
        Vector2 frameSize = slotSize + framePadding;
        _highlightFrame.Size = frameSize;
        _highlightFrame.Position = -frameSize * 0.5f;

        // Top-right corner of the card (right-aligned HBoxContainer hugging upper-right edge)
        _debuffBadgeRow.Size = new Vector2(slotSize.X, 22f);
        _debuffBadgeRow.CustomMinimumSize = new Vector2(slotSize.X, 20f);
        _debuffBadgeRow.Position = new Vector2(-slotSize.X * 0.5f + 5f, -slotSize.Y * 0.5f - 6f);

        if (_cardNode != null && GodotObject.IsInstanceValid(_cardNode))
        {
            _cardNode.Scale = Vector2.One * _baseScale;
            _cardNode.Position = -_cardNode.Size * 0.5f * _baseScale;
        }
    }

    private void AttachOrUpdateCard(CardModel card)
    {
        if (_cardNode == null || !GodotObject.IsInstanceValid(_cardNode))
        {
            _cardNode = NCard.Create(card, ModelVisibility.Visible);
            if (_cardNode != null)
            {
                _cardNode.Visible = true;
                _cardNode.Rotation = 0f;
                _cardNode.Modulate = Colors.White;
                _cardNode.SelfModulate = Colors.White;
                _cardNode.Scale = Vector2.One * _baseScale;
                _visualRoot.AddChild(_cardNode);
                _visualRoot.MoveChild(_cardNode, 0);
                _cardNode.Position = -_cardNode.Size * 0.5f * _baseScale;
                _cardNode.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
                _cardNode.CardHighlight?.AnimHideInstantly();
                _vanillaHighlightShown = false;
                SetMouseIgnoreRecursive(_cardNode);
            }
        }
        else
        {
            _cardNode.Visibility = ModelVisibility.Visible;
            if (_cardNode.Model != card)
            {
                _cardNode.Model = card;
            }
            _cardNode.Visible = true;
            _cardNode.Rotation = 0f;
            _cardNode.SelfModulate = Colors.White;
            _cardNode.Scale = Vector2.One * _baseScale;
            _cardNode.Position = -_cardNode.Size * 0.5f * _baseScale;
            _cardNode.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            SetMouseIgnoreRecursive(_cardNode);
        }

        ApplyCardHighlightAndState(card);
    }

    private void ApplyCardHighlightAndState(CardModel card)
    {
        _debuffAnalysis = DebuffAnalyzer.AnalyzeCard(card);
        _isPlayable = EvaluatePlayableByOwner(card);
        ClearDebuffBadges();

        if (_debuffAnalysis.HasEnemyDebuff && _debuffAnalysis.PrimaryHighlightColor is Color debuffColor)
        {
            // Debuff card: hide vanilla cyan highlight so our debuff color border is unmistakable
            HideVanillaHighlight(instant: true);

            if (_isPlayable)
            {
                // Playable debuff card: keep original bright face + pulsing colored outer frame
                if (_cardNode != null && GodotObject.IsInstanceValid(_cardNode))
                {
                    _cardNode.Modulate = Colors.White;
                }

                var style = new StyleBoxFlat
                {
                    DrawCenter = false,
                    BorderColor = debuffColor,
                    ShadowColor = new Color(debuffColor.R, debuffColor.G, debuffColor.B, 0.55f),
                    ShadowSize = 8
                };
                style.SetBorderWidthAll(3);
                style.SetCornerRadiusAll(8);
                _highlightFrame.AddThemeStyleboxOverride("panel", style);
                _highlightFrame.Visible = true;
                _highlightFrame.Modulate = Colors.White;

                _pulseTween?.Kill();
                _pulseTween = CreateTween().SetLoops();
                _pulseTween.TweenProperty(_highlightFrame, "modulate:a", 0.62f, 0.85)
                    .SetTrans(Tween.TransitionType.Sine)
                    .SetEase(Tween.EaseType.InOut);
                _pulseTween.TweenProperty(_highlightFrame, "modulate:a", 1.0f, 0.85)
                    .SetTrans(Tween.TransitionType.Sine)
                    .SetEase(Tween.EaseType.InOut);

                PopulateDebuffBadges(dimmed: false);
            }
            else
            {
                // Unplayable debuff card: darkened card face + static colored border matching debuff color
                if (_cardNode != null && GodotObject.IsInstanceValid(_cardNode))
                {
                    _cardNode.Modulate = UnplayableDebuffTint;
                }

                Color mutedBorder = new(debuffColor.R * 0.82f, debuffColor.G * 0.82f, debuffColor.B * 0.82f, 0.85f);
                var style = new StyleBoxFlat
                {
                    DrawCenter = false,
                    BorderColor = mutedBorder,
                    ShadowColor = new Color(debuffColor.R, debuffColor.G, debuffColor.B, 0.25f),
                    ShadowSize = 4
                };
                style.SetBorderWidthAll(3);
                style.SetCornerRadiusAll(8);
                _highlightFrame.AddThemeStyleboxOverride("panel", style);
                _highlightFrame.Visible = true;
                _highlightFrame.Modulate = Colors.White;

                _pulseTween?.Kill();
                _pulseTween = null;

                PopulateDebuffBadges(dimmed: true);
            }

            return;
        }

        // Non-debuff card: restore normal tint, hide custom debuff frame, and use vanilla NCardHighlight
        _highlightFrame.Visible = false;
        _debuffBadgeRow.Visible = false;
        _pulseTween?.Kill();
        _pulseTween = null;

        if (_cardNode != null && GodotObject.IsInstanceValid(_cardNode))
        {
            _cardNode.Modulate = Colors.White;

            bool shouldGlowRed = card.ShouldGlowRed;
            bool shouldGlowGold = _isPlayable && card.ShouldGlowGold;
            if (_isPlayable || shouldGlowRed || shouldGlowGold)
            {
                Color vanillaColor = NCardHighlight.playableColor;
                if (shouldGlowRed)
                {
                    vanillaColor = NCardHighlight.red;
                }
                else if (shouldGlowGold)
                {
                    vanillaColor = NCardHighlight.gold;
                }

                if (_cardNode.CardHighlight != null && GodotObject.IsInstanceValid(_cardNode.CardHighlight))
                {
                    _cardNode.CardHighlight.Modulate = vanillaColor;
                    if (!_vanillaHighlightShown)
                    {
                        _cardNode.CardHighlight.AnimShow();
                        _vanillaHighlightShown = true;
                    }
                }
            }
            else
            {
                HideVanillaHighlight(instant: false);
            }
        }
    }

    private void HideVanillaHighlight(bool instant)
    {
        if (_cardNode?.CardHighlight == null || !GodotObject.IsInstanceValid(_cardNode.CardHighlight))
        {
            _vanillaHighlightShown = false;
            return;
        }

        if (instant)
        {
            _cardNode.CardHighlight.AnimHideInstantly();
            _vanillaHighlightShown = false;
        }
        else if (_vanillaHighlightShown)
        {
            _cardNode.CardHighlight.AnimHide();
            _vanillaHighlightShown = false;
        }
    }

    private static bool EvaluatePlayableByOwner(CardModel card)
    {
        try
        {
            if (card.Owner == null || !CombatManager.Instance.IsInProgress)
            {
                return false;
            }

            if (CombatManager.Instance.IsPlayerReadyToEndTurn(card.Owner))
            {
                return false;
            }

            return card.CanPlay();
        }
        catch
        {
            return false;
        }
    }

    private void PopulateDebuffBadges(bool dimmed)
    {
        bool addedIcon = false;
        foreach (DebuffInfo debuff in _debuffAnalysis.Debuffs)
        {
            Control badge = CreateDebuffMiniBadge(debuff, dimmed);
            _debuffBadgeRow.AddChild(badge);
            addedIcon = true;
        }
        _debuffBadgeRow.Visible = addedIcon;
    }

    private static Control CreateDebuffMiniBadge(DebuffInfo debuff, bool dimmed)
    {
        var panel = new PanelContainer
        {
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
            Modulate = dimmed ? new Color(0.75f, 0.75f, 0.78f, 0.92f) : Colors.White
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.08f, 0.10f, 0.90f),
            BorderColor = debuff.HighlightColor,
            ContentMarginLeft = 2,
            ContentMarginRight = 2,
            ContentMarginTop = 2,
            ContentMarginBottom = 2
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(6);
        panel.AddThemeStyleboxOverride("panel", style);

        if (debuff.Icon != null)
        {
            var iconRect = new TextureRect
            {
                Texture = debuff.Icon,
                CustomMinimumSize = new Vector2(17f, 17f),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore
            };
            panel.AddChild(iconRect);
        }
        else
        {
            var label = new Label
            {
                Text = debuff.DisplayName,
                MouseFilter = MouseFilterEnum.Ignore
            };
            label.AddThemeColorOverride("font_color", debuff.HighlightColor);
            label.AddThemeFontSizeOverride("font_size", 10);
            panel.AddChild(label);
        }

        return panel;
    }

    private void ClearDebuffBadges()
    {
        foreach (Node child in _debuffBadgeRow.GetChildren())
        {
            child.QueueFree();
        }
    }

    private void OnMouseEntered()
    {
        if (_isExiting || NHoverTipSet.shouldBlockHoverTips || NTargetManager.Instance.IsInSelection)
        {
            return;
        }

        _isHovered = true;
        HoverStateChanged?.Invoke(true);

        Vector2 targetVisualPos = GetRestVisualPosition();
        if (CardReminderSettings.HoverZoomEnabled)
        {
            ZIndex = 10;
            targetVisualPos = ComputeClampedZoomCenter(HoverZoomFactor);

            _zoomTween?.Kill();
            _zoomTween = CreateTween().SetParallel();
            _zoomTween.TweenProperty(_visualRoot, "scale", Vector2.One * HoverZoomFactor, 0.14)
                .SetTrans(Tween.TransitionType.Cubic)
                .SetEase(Tween.EaseType.Out);
            _zoomTween.TweenProperty(_visualRoot, "position", targetVisualPos, 0.14)
                .SetTrans(Tween.TransitionType.Cubic)
                .SetEase(Tween.EaseType.Out);
        }

        if (_cardModel != null)
        {
            NHoverTipSet? tips = NHoverTipSet.CreateAndShow(this, _cardModel.HoverTips, HoverTipAlignment.Right);
            if (tips != null)
            {
                Vector2 zoomedHalf = Size * (CardReminderSettings.HoverZoomEnabled ? HoverZoomFactor * 0.5f : 0.5f);
                Vector2 cardCenterGlobal = GlobalPosition + targetVisualPos;
                tips.SetGlobalPosition(new Vector2(cardCenterGlobal.X + zoomedHalf.X + 16f, Math.Max(78f, cardCenterGlobal.Y - zoomedHalf.Y)));
            }
        }
    }

    private void OnMouseExited()
    {
        if (_isExiting)
        {
            return;
        }

        _isHovered = false;
        HoverStateChanged?.Invoke(false);
        NHoverTipSet.Remove(this);

        _zoomTween?.Kill();
        _zoomTween = CreateTween().SetParallel();
        _zoomTween.TweenProperty(_visualRoot, "scale", Vector2.One, 0.16)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);
        _zoomTween.TweenProperty(_visualRoot, "position", GetRestVisualPosition(), 0.16)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);
        _zoomTween.Chain().TweenCallback(Callable.From(() =>
        {
            if (!_isHovered && GodotObject.IsInstanceValid(this))
            {
                ZIndex = 0;
            }
        }));
    }

    /// <summary>
    /// Keeps the 3x zoomed card centered in-place while leaving clearance below the TopBar
    /// (so local player's TopBar potion menu/icons are never covered) and within screen edges.
    /// </summary>
    private Vector2 ComputeClampedZoomCenter(float zoomFactor)
    {
        Vector2 restCenter = GetRestVisualPosition();
        if (!IsInsideTree())
        {
            return restCenter;
        }

        Rect2 visibleRect = GetCanvasTransform().AffineInverse() * GetViewportRect();
        Vector2 halfZoomed = Size * (zoomFactor * 0.5f);
        Vector2 globalCenter = GlobalPosition + restCenter;

        const float sideMargin = 14f;
        const float topBarSafeMargin = 78f;
        float clampedX = Mathf.Clamp(
            globalCenter.X,
            visibleRect.Position.X + halfZoomed.X + sideMargin,
            Mathf.Max(visibleRect.Position.X + halfZoomed.X + sideMargin, visibleRect.End.X - halfZoomed.X - sideMargin));
        float clampedY = Mathf.Clamp(
            globalCenter.Y,
            visibleRect.Position.Y + halfZoomed.Y + topBarSafeMargin,
            Mathf.Max(visibleRect.Position.Y + halfZoomed.Y + topBarSafeMargin, visibleRect.End.Y - halfZoomed.Y - sideMargin));

        return restCenter + new Vector2(clampedX - globalCenter.X, clampedY - globalCenter.Y);
    }

    private static void SetMouseIgnoreRecursive(Node root)
    {
        if (root is Control ctrl)
        {
            ctrl.MouseFilter = MouseFilterEnum.Ignore;
        }

        foreach (Node child in root.GetChildren())
        {
            SetMouseIgnoreRecursive(child);
        }
    }
}
