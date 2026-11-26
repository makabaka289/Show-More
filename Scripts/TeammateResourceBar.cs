using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace CardReminder.Scripts;

/// <summary>
/// Displays a teammate's current Gold (with smooth count-up/down transition) and owned Potions
/// (no empty slot boxes; newly obtained potions glide smoothly from above over 1.0s)
/// to the right of their Player ID. Hidden for the local player.
/// </summary>
public sealed partial class TeammateResourceBar : HBoxContainer
{
    private const string NativeGoldIconPath = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_gold.tres";
    private const double PotionDropDurationSeconds = 1.0;
    private const float PotionDropStartOffsetY = -36f;
    private const float PotionIconSize = 22f;
    private const int PotionSpacing = 4;
    private const float GoldAreaFixedWidth = 68f;

    private static Texture2D? _cachedGoldTexture;

    private Player? _player;
    private Control _goldContainer = null!;
    private Label _goldLabel = null!;
    private Control _potionZone = null!;
    private HBoxContainer _potionContainer = null!;

    private readonly List<Control> _potionNodes = [];
    private readonly List<PotionModel> _previousPotionSnapshot = [];
    private bool _hasInitializedPotions;

    private int _displayedGold;
    private bool _hasInitializedGold;
    private Tween? _goldValueTween;
    private Tween? _goldScaleTween;

    public event Action? LayoutWidthChanged;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 6);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = false;

        // Subtle separator bar between native player name/icons and our gold+potion block
        var divider = new Panel
        {
            CustomMinimumSize = new Vector2(1f, 14f),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore
        };
        divider.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(1f, 0.92f, 0.72f, 0.25f)
        });
        AddChild(divider);

        // Gold indicator inside a fixed-width zone so gold digit changes never push potions
        var goldBox = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(GoldAreaFixedWidth, 22f),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = false
        };
        goldBox.AddThemeConstantOverride("separation", 3);
        _goldContainer = goldBox;

        Texture2D? goldTexture = ResolveGoldIcon();
        if (goldTexture != null)
        {
            var goldIcon = new TextureRect
            {
                Texture = goldTexture,
                CustomMinimumSize = new Vector2(18f, 18f),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                MouseFilter = MouseFilterEnum.Ignore
            };
            goldBox.AddChild(goldIcon);
        }
        else
        {
            var fallbackDot = new Panel
            {
                CustomMinimumSize = new Vector2(12f, 12f),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                MouseFilter = MouseFilterEnum.Ignore
            };
            var dotStyle = new StyleBoxFlat { BgColor = StsColors.gold };
            dotStyle.SetCornerRadiusAll(6);
            fallbackDot.AddThemeStyleboxOverride("panel", dotStyle);
            goldBox.AddChild(fallbackDot);
        }

        _goldLabel = new Label
        {
            Text = "0",
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _goldLabel.AddThemeColorOverride("font_color", StsColors.gold);
        _goldLabel.AddThemeColorOverride("font_outline_color", new Color(0.08f, 0.06f, 0.05f, 0.95f));
        _goldLabel.AddThemeConstantOverride("outline_size", 4);
        _goldLabel.AddThemeFontSizeOverride("font_size", 16);
        goldBox.AddChild(_goldLabel);

        AddChild(goldBox);

        // Dedicated independent container space reserved exclusively for potions
        // so gold changes or other HUD elements never squeeze or push potions.
        _potionZone = new Control
        {
            Name = "DedicatedPotionZone",
            CustomMinimumSize = new Vector2(ComputeReservedPotionZoneWidth(3), PotionIconSize + 4f),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = false
        };
        AddChild(_potionZone);

        // Owned potions strip inside the dedicated potion zone (no empty slot frames)
        _potionContainer = new HBoxContainer
        {
            Name = "OwnedPotionsRow",
            Position = new Vector2(2f, 2f),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = false
        };
        _potionContainer.AddThemeConstantOverride("separation", PotionSpacing);
        _potionZone.AddChild(_potionContainer);

        RefreshAll();
    }

    public void Bind(Player player)
    {
        if (_player == player)
        {
            RefreshAll();
            return;
        }

        Unsubscribe();
        _player = player;
        _hasInitializedGold = false;
        _hasInitializedPotions = false;
        _previousPotionSnapshot.Clear();

        // Only subscribe and display for teammates (never for the local player)
        if (!LocalContext.IsMe(player))
        {
            Subscribe();
        }

        RefreshAll();
    }

    public override void _ExitTree()
    {
        Unsubscribe();
        _goldValueTween?.Kill();
        _goldScaleTween?.Kill();
        ClearPotionNodes();
        base._ExitTree();
    }

    public void RefreshAll()
    {
        if (!IsNodeReady())
        {
            return;
        }

        bool isTeammate = _player != null && !LocalContext.IsMe(_player);
        Visible = CardReminderSettings.ShowGoldAndPotions && isTeammate;
        if (!Visible || _player == null)
        {
            LayoutWidthChanged?.Invoke();
            return;
        }

        RefreshGold(animate: _hasInitializedGold);
        RefreshPotions(animateNewPotions: _hasInitializedPotions);
        LayoutWidthChanged?.Invoke();
    }

    /// <summary>
    /// Computes an immediate conservative width estimate so sibling UI (like cards to the right)
    /// never overlaps newly added potions even before Godot completes its next layout pass.
    /// </summary>
    public float EstimateRequiredWidth()
    {
        if (!Visible || _player == null)
        {
            return 0f;
        }

        float measured = Math.Max(Size.X, GetCombinedMinimumSize().X);
        int reservedSlots = Math.Max(_player.MaxPotionCount, _potionNodes.Count);
        float potionZoneWidth = ComputeReservedPotionZoneWidth(reservedSlots);
        float estimated = 1f + 6f + GoldAreaFixedWidth + 6f + potionZoneWidth;
        return Math.Max(measured, estimated);
    }

    private static float ComputeReservedPotionZoneWidth(int slotCapacity)
    {
        int count = Math.Max(1, slotCapacity);
        return count * PotionIconSize + Math.Max(0, count - 1) * PotionSpacing + 8f;
    }

    private void Subscribe()
    {
        if (_player == null)
        {
            return;
        }

        _player.GoldChanged += OnGoldChanged;
        _player.PotionProcured += OnPotionChanged;
        _player.PotionDiscarded += OnPotionChanged;
        _player.UsedPotionRemoved += OnPotionChanged;
        _player.MaxPotionCountChanged += OnMaxPotionCountChanged;
    }

    private void Unsubscribe()
    {
        if (_player == null)
        {
            return;
        }

        _player.GoldChanged -= OnGoldChanged;
        _player.PotionProcured -= OnPotionChanged;
        _player.PotionDiscarded -= OnPotionChanged;
        _player.UsedPotionRemoved -= OnPotionChanged;
        _player.MaxPotionCountChanged -= OnMaxPotionCountChanged;
    }

    private void OnGoldChanged()
    {
        if (IsInsideTree() && Visible)
        {
            RefreshGold(animate: true);
            LayoutWidthChanged?.Invoke();
        }
    }

    private void OnPotionChanged(PotionModel _)
    {
        if (IsInsideTree())
        {
            CallDeferred(nameof(RefreshPotionsDeferred));
        }
    }

    private void OnMaxPotionCountChanged(int _)
    {
        if (IsInsideTree())
        {
            CallDeferred(nameof(RefreshPotionsDeferred));
        }
    }

    private void RefreshPotionsDeferred()
    {
        if (GodotObject.IsInstanceValid(this) && IsInsideTree() && Visible)
        {
            RefreshPotions(animateNewPotions: true);
            LayoutWidthChanged?.Invoke();
        }
    }

    private void RefreshGold(bool animate)
    {
        if (_player == null || !GodotObject.IsInstanceValid(_goldLabel))
        {
            return;
        }

        int targetGold = _player.Gold;

        if (!_hasInitializedGold || !animate)
        {
            _hasInitializedGold = true;
            _displayedGold = targetGold;
            _goldValueTween?.Kill();
            _goldLabel.Text = targetGold.ToString();
            return;
        }

        int startGold = _displayedGold;
        int delta = targetGold - startGold;
        if (delta == 0)
        {
            _goldLabel.Text = targetGold.ToString();
            return;
        }

        _goldValueTween?.Kill();
        double duration = Math.Clamp(0.35 + Math.Abs(delta) * 0.004, 0.35, 0.85);
        _goldValueTween = CreateTween();
        _goldValueTween.TweenMethod(
            Callable.From<float>(v =>
            {
                if (!GodotObject.IsInstanceValid(_goldLabel))
                {
                    return;
                }
                _displayedGold = Mathf.RoundToInt(v);
                _goldLabel.Text = _displayedGold.ToString();
            }),
            (float)startGold,
            (float)targetGold,
            duration)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);
        _goldValueTween.TweenCallback(Callable.From(() =>
        {
            _displayedGold = targetGold;
            if (GodotObject.IsInstanceValid(_goldLabel))
            {
                _goldLabel.Text = targetGold.ToString();
            }
            LayoutWidthChanged?.Invoke();
        }));

        _goldScaleTween?.Kill();
        _goldLabel.PivotOffset = _goldLabel.Size * 0.5f;
        _goldScaleTween = CreateTween();
        _goldScaleTween.TweenProperty(_goldLabel, "scale", new Vector2(1.18f, 1.18f), 0.12)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.Out);
        _goldScaleTween.TweenProperty(_goldLabel, "scale", Vector2.One, 0.22)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);

        SpawnGoldDeltaFloat(delta);
    }

    private void SpawnGoldDeltaFloat(int delta)
    {
        if (!GodotObject.IsInstanceValid(_goldLabel) || !IsInsideTree())
        {
            return;
        }

        // TopLevel=true detaches from HBoxContainer layout completely so it never pushes potions aside,
        // and high ZIndex ensures it renders cleanly above surrounding UI elements.
        var popup = new Label
        {
            Text = delta > 0 ? $"+{delta}" : delta.ToString(),
            TopLevel = true,
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 120,
            ZAsRelative = false
        };
        Color color = delta > 0 ? new Color(1.0f, 0.90f, 0.38f, 1f) : new Color(1.0f, 0.52f, 0.52f, 1f);
        popup.AddThemeColorOverride("font_color", color);
        popup.AddThemeColorOverride("font_outline_color", new Color(0.06f, 0.05f, 0.04f, 0.95f));
        popup.AddThemeConstantOverride("outline_size", 4);
        popup.AddThemeFontSizeOverride("font_size", 15);

        Vector2 startGlobalPos = _goldLabel.GlobalPosition + new Vector2(0f, -18f);
        popup.GlobalPosition = startGlobalPos;
        popup.Modulate = new Color(1f, 1f, 1f, 0f);

        AddChild(popup);

        Tween tween = popup.CreateTween().SetParallel();
        tween.TweenProperty(popup, "modulate:a", 1f, 0.12)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.Out);
        tween.TweenProperty(popup, "global_position:y", startGlobalPos.Y - 22f, 0.80)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);
        tween.TweenProperty(popup, "modulate:a", 0f, 0.28)
            .SetDelay(0.52)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.In);
        tween.Chain().TweenCallback(Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(popup))
            {
                popup.QueueFree();
            }
        }));
    }

    private void RefreshPotions(bool animateNewPotions)
    {
        if (_player == null || !GodotObject.IsInstanceValid(_potionContainer))
        {
            return;
        }

        var previousSet = new HashSet<PotionModel>(ReferenceEqualityComparer.Instance);
        foreach (PotionModel prev in _previousPotionSnapshot)
        {
            previousSet.Add(prev);
        }

        ClearPotionNodes();

        var ownedPotions = new List<PotionModel>();
        foreach (PotionModel? p in _player.PotionSlots)
        {
            if (p != null)
            {
                ownedPotions.Add(p);
            }
        }

        _potionContainer.Visible = ownedPotions.Count > 0;

        int reservedSlots = Math.Max(_player.MaxPotionCount, ownedPotions.Count);
        float zoneWidth = ComputeReservedPotionZoneWidth(reservedSlots);
        if (GodotObject.IsInstanceValid(_potionZone))
        {
            _potionZone.CustomMinimumSize = new Vector2(zoneWidth, PotionIconSize + 4f);
            _potionZone.Size = _potionZone.CustomMinimumSize;
        }

        foreach (PotionModel potion in ownedPotions)
        {
            bool isNewlyAdded = animateNewPotions && _hasInitializedPotions && !previousSet.Contains(potion);
            Control potionNode = CreatePotionIconNode(potion, isNewlyAdded);
            _potionContainer.AddChild(potionNode);
            _potionNodes.Add(potionNode);
        }

        _previousPotionSnapshot.Clear();
        _previousPotionSnapshot.AddRange(ownedPotions);
        _hasInitializedPotions = true;
    }

    private static Control CreatePotionIconNode(PotionModel potion, bool animateDropIn)
    {
        var holder = new Control
        {
            CustomMinimumSize = new Vector2(PotionIconSize, PotionIconSize),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Stop,
            MouseDefaultCursorShape = CursorShape.Help,
            ClipContents = false
        };

        var visual = new Control
        {
            Size = new Vector2(PotionIconSize, PotionIconSize),
            PivotOffset = new Vector2(PotionIconSize * 0.5f, PotionIconSize * 0.5f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        visual.SetAnchorsPreset(LayoutPreset.FullRect);
        holder.AddChild(visual);

        Texture2D? outlineTex = potion.Outline;
        if (outlineTex != null)
        {
            var outline = new TextureRect
            {
                Texture = outlineTex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore
            };
            outline.SetAnchorsPreset(LayoutPreset.FullRect);
            visual.AddChild(outline);
        }

        var image = new TextureRect
        {
            Texture = potion.Image,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        image.SetAnchorsPreset(LayoutPreset.FullRect);
        visual.AddChild(image);

        if (animateDropIn)
        {
            visual.Position = new Vector2(0f, PotionDropStartOffsetY);
            visual.Modulate = new Color(1f, 1f, 1f, 0f);
            visual.Scale = new Vector2(0.85f, 0.85f);
            visual.ZIndex = 25;

            holder.TreeEntered += StartDropTween;
            void StartDropTween()
            {
                holder.TreeEntered -= StartDropTween;
                if (!GodotObject.IsInstanceValid(visual))
                {
                    return;
                }

                Tween tween = visual.CreateTween().SetParallel();
                tween.TweenProperty(visual, "modulate:a", 1f, 0.25)
                    .SetTrans(Tween.TransitionType.Quad)
                    .SetEase(Tween.EaseType.Out);
                tween.TweenProperty(visual, "position:y", 0f, PotionDropDurationSeconds)
                    .SetTrans(Tween.TransitionType.Cubic)
                    .SetEase(Tween.EaseType.Out);
                tween.TweenProperty(visual, "scale", Vector2.One, 0.45)
                    .SetTrans(Tween.TransitionType.Back)
                    .SetEase(Tween.EaseType.Out);
                tween.Chain().TweenCallback(Callable.From(() =>
                {
                    if (GodotObject.IsInstanceValid(visual))
                    {
                        visual.ZIndex = 0;
                    }
                }));
            }
        }

        void OnEnter()
        {
            if (!GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree())
            {
                return;
            }

            NHoverTipSet? tipSet = NHoverTipSet.CreateAndShow(holder, potion.HoverTips, HoverTipAlignment.Right);
            tipSet?.SetGlobalPosition(holder.GlobalPosition + new Vector2(holder.Size.X + 10f, 0f));
        }

        void OnExit()
        {
            if (GodotObject.IsInstanceValid(holder))
            {
                NHoverTipSet.Remove(holder);
            }
        }

        holder.MouseEntered += OnEnter;
        holder.MouseExited += OnExit;
        holder.TreeExiting += () =>
        {
            holder.MouseEntered -= OnEnter;
            holder.MouseExited -= OnExit;
            NHoverTipSet.Remove(holder);
        };

        return holder;
    }

    private void ClearPotionNodes()
    {
        foreach (Control node in _potionNodes)
        {
            if (GodotObject.IsInstanceValid(node))
            {
                NHoverTipSet.Remove(node);
                node.QueueFree();
            }
        }

        _potionNodes.Clear();
    }

    private static Texture2D? ResolveGoldIcon()
    {
        if (_cachedGoldTexture != null && GodotObject.IsInstanceValid(_cachedGoldTexture))
        {
            return _cachedGoldTexture;
        }

        if (ResourceLoader.Exists(NativeGoldIconPath))
        {
            _cachedGoldTexture = ResourceLoader.Load<Texture2D>(NativeGoldIconPath, null, ResourceLoader.CacheMode.Reuse);
        }

        return _cachedGoldTexture;
    }
}
