using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;

namespace CardReminder.Scripts;

/// <summary>
/// Full-screen click-anywhere-to-dismiss reminder overlay displayed when playing a card
/// while teammates hold enemy-debuff cards (e.g. Vulnerable / Weak).
/// </summary>
public sealed partial class PlayCardDebuffReminder : Control
{
    private static PlayCardDebuffReminder? _activeOverlay;

    private const ulong DismissGraceMsec = 120uL;

    private PanelContainer _cardPanel = null!;
    private bool _dismissing;
    private ulong _shownAtMsec;

    public static bool IsShowing => _activeOverlay != null && GodotObject.IsInstanceValid(_activeOverlay) && _activeOverlay.IsInsideTree();

    public static void Show(IReadOnlyList<TeammateDebuffSummary> teammates, bool wasPlayIntercepted)
    {
        if (teammates.Count == 0)
        {
            return;
        }

        DismissActive();

        Node? host = ResolveOverlayHost();
        if (host == null)
        {
            return;
        }

        var overlay = new PlayCardDebuffReminder();
        overlay.BuildContent(teammates, wasPlayIntercepted);
        host.AddChild(overlay);
        _activeOverlay = overlay;
    }

    public static void DismissActive()
    {
        if (_activeOverlay != null && GodotObject.IsInstanceValid(_activeOverlay))
        {
            _activeOverlay.QueueFree();
        }
        _activeOverlay = null;
    }

    public override void _Ready()
    {
        Name = "CardReminderDebuffOverlay";
        _shownAtMsec = Time.GetTicksMsec();
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 240;
        ZAsRelative = false;

        GuiInput += OnOverlayGuiInput;

        // Subtle pop-in animation
        Modulate = new Color(1f, 1f, 1f, 0f);
        if (GodotObject.IsInstanceValid(_cardPanel))
        {
            _cardPanel.Scale = new Vector2(0.92f, 0.92f);
        }

        Tween tween = CreateTween().SetParallel();
        tween.TweenProperty(this, "modulate:a", 1f, 0.14)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);
        if (GodotObject.IsInstanceValid(_cardPanel))
        {
            tween.TweenProperty(_cardPanel, "scale", Vector2.One, 0.16)
                .SetTrans(Tween.TransitionType.Back)
                .SetEase(Tween.EaseType.Out);
        }
    }

    public override void _ExitTree()
    {
        GuiInput -= OnOverlayGuiInput;
        if (_activeOverlay == this)
        {
            _activeOverlay = null;
        }
        base._ExitTree();
    }

    public override void _Input(InputEvent @event)
    {
        if (_dismissing || !IsVisibleInTree() || Time.GetTicksMsec() - _shownAtMsec < DismissGraceMsec)
        {
            return;
        }

        if (@event is InputEventMouseButton { Pressed: true })
        {
            GetViewport()?.SetInputAsHandled();
            DismissWithFade();
            return;
        }

        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape or Key.Space or Key.Enter })
        {
            GetViewport()?.SetInputAsHandled();
            DismissWithFade();
        }
    }

    private void OnOverlayGuiInput(InputEvent @event)
    {
        if (_dismissing || Time.GetTicksMsec() - _shownAtMsec < DismissGraceMsec)
        {
            return;
        }

        if (@event is InputEventMouseButton { Pressed: true })
        {
            AcceptEvent();
            DismissWithFade();
        }
    }

    private void DismissWithFade()
    {
        if (_dismissing)
        {
            return;
        }

        _dismissing = true;
        MouseFilter = MouseFilterEnum.Ignore;

        Tween tween = CreateTween();
        tween.TweenProperty(this, "modulate:a", 0f, 0.10)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.Out);
        tween.TweenCallback(Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(this))
            {
                QueueFree();
            }
        }));
    }

    private void BuildContent(IReadOnlyList<TeammateDebuffSummary> teammates, bool wasPlayIntercepted)
    {
        // Soft dim backdrop across screen so clicking anywhere cancels
        var backdrop = new ColorRect
        {
            Color = new Color(0.02f, 0.02f, 0.03f, 0.30f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);

        // Centered container positioned slightly above screen center so it doesn't cover hand cards
        var anchorBox = new CenterContainer
        {
            MouseFilter = MouseFilterEnum.Ignore
        };
        anchorBox.SetAnchorsPreset(LayoutPreset.FullRect);
        anchorBox.OffsetBottom = -160f;
        AddChild(anchorBox);

        _cardPanel = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Ignore
        };

        var panelStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.11f, 0.09f, 0.12f, 0.96f),
            BorderColor = new Color(0.84f, 0.65f, 0.36f, 0.95f),
            ContentMarginLeft = 24,
            ContentMarginRight = 24,
            ContentMarginTop = 16,
            ContentMarginBottom = 16,
            ShadowColor = new Color(0f, 0f, 0f, 0.65f),
            ShadowSize = 14,
            ShadowOffset = new Vector2(0f, 6f)
        };
        panelStyle.SetBorderWidthAll(2);
        panelStyle.SetCornerRadiusAll(10);
        _cardPanel.AddThemeStyleboxOverride("panel", panelStyle);

        var rootColumn = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore
        };
        rootColumn.AddThemeConstantOverride("separation", 10);
        _cardPanel.AddChild(rootColumn);

        // Header
        var titleLabel = new Label
        {
            Text = "队友减益卡牌提醒",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        titleLabel.AddThemeColorOverride("font_color", StsColors.gold);
        titleLabel.AddThemeFontSizeOverride("font_size", 22);
        rootColumn.AddChild(titleLabel);

        // Divider
        var sep = new Panel
        {
            CustomMinimumSize = new Vector2(280f, 1f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        sep.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.84f, 0.65f, 0.36f, 0.35f) });
        rootColumn.AddChild(sep);

        // One row per teammate: [CharacterIcon] {PlayerName} 有 [Icon][DebuffName] ...
        foreach (TeammateDebuffSummary teammate in teammates)
        {
            var row = new HBoxContainer
            {
                Alignment = BoxContainer.AlignmentMode.Center,
                MouseFilter = MouseFilterEnum.Ignore
            };
            row.AddThemeConstantOverride("separation", 8);

            if (teammate.Player.Character.IconTexture != null)
            {
                var charIcon = new TextureRect
                {
                    Texture = teammate.Player.Character.IconTexture,
                    CustomMinimumSize = new Vector2(28f, 28f),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    SizeFlagsVertical = SizeFlags.ShrinkCenter,
                    MouseFilter = MouseFilterEnum.Ignore
                };
                row.AddChild(charIcon);
            }

            var playerLabel = new Label
            {
                Text = teammate.PlayerName,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore
            };
            playerLabel.AddThemeColorOverride("font_color", StsColors.gold);
            playerLabel.AddThemeFontSizeOverride("font_size", 20);
            row.AddChild(playerLabel);

            var hasLabel = new Label
            {
                Text = "有",
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore
            };
            hasLabel.AddThemeColorOverride("font_color", StsColors.cream);
            hasLabel.AddThemeFontSizeOverride("font_size", 19);
            row.AddChild(hasLabel);

            foreach (DebuffInfo debuff in teammate.Debuffs)
            {
                row.AddChild(CreateDebuffPill(debuff));
            }

            rootColumn.AddChild(row);
        }

        // Dismiss hint
        var hintLabel = new Label
        {
            Text = wasPlayIntercepted
                ? "已暂停本次出牌 · 点击任意位置关闭（再次打出将直接生效）"
                : "点击任意位置关闭",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        hintLabel.AddThemeColorOverride("font_color", new Color(0.78f, 0.74f, 0.66f, 0.90f));
        hintLabel.AddThemeFontSizeOverride("font_size", 14);
        rootColumn.AddChild(hintLabel);

        anchorBox.AddChild(_cardPanel);
    }

    private static Control CreateDebuffPill(DebuffInfo debuff)
    {
        var pill = new PanelContainer
        {
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(debuff.HighlightColor.R * 0.22f, debuff.HighlightColor.G * 0.22f, debuff.HighlightColor.B * 0.22f, 0.92f),
            BorderColor = debuff.HighlightColor,
            ContentMarginLeft = 8,
            ContentMarginRight = 10,
            ContentMarginTop = 4,
            ContentMarginBottom = 4
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(8);
        pill.AddThemeStyleboxOverride("panel", style);

        var inner = new HBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore
        };
        inner.AddThemeConstantOverride("separation", 5);

        if (debuff.Icon != null)
        {
            var icon = new TextureRect
            {
                Texture = debuff.Icon,
                CustomMinimumSize = new Vector2(22f, 22f),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                MouseFilter = MouseFilterEnum.Ignore
            };
            inner.AddChild(icon);
        }

        var nameLabel = new Label
        {
            Text = debuff.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        nameLabel.AddThemeColorOverride("font_color", debuff.HighlightColor.Lightened(0.20f));
        nameLabel.AddThemeFontSizeOverride("font_size", 18);
        inner.AddChild(nameLabel);

        pill.AddChild(inner);
        return pill;
    }

    private static Node? ResolveOverlayHost()
    {
        if (NRun.Instance?.GlobalUi is { } globalUi && GodotObject.IsInstanceValid(globalUi))
        {
            return globalUi;
        }

        if (NGame.Instance is { } game && GodotObject.IsInstanceValid(game))
        {
            return game;
        }

        return null;
    }
}
