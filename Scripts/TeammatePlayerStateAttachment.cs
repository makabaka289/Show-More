using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Rooms;

namespace CardReminder.Scripts;

/// <summary>
/// Attached to each NMultiplayerPlayerState via RitsuLib's ModNodeAttachmentRegistry.
/// Adds Gold + Potion slots to the right of teammate Player IDs and places the live Hand Card tray
/// to the right of the player entry with smooth height stability across turns.
/// </summary>
public sealed partial class TeammatePlayerStateAttachment : Node
{
    public const string AttachmentId = "teammate_player_state_ui";
    public const string AttachmentNodeName = "CardReminderPlayerStateAttachment";

    private const float CardTrayGapRight = 14f;

    private NMultiplayerPlayerState? _playerState;
    private Player? _player;
    private NHealthBar? _healthBar;
    private HBoxContainer? _topContainer;

    private TeammateResourceBar? _resourceBar;
    private TeammateCardTray? _cardTray;

    private PlayerCombatState? _subscribedCombatState;
    private Vector2 _baseMinSize;
    private float _headerHeight;
    private bool _refreshQueued;
    private bool _isCardHovered;
    private Tween? _trayPosTween;

    public float HeaderHeight => _headerHeight > 0f ? _headerHeight : 64f;

    public void Bind(NMultiplayerPlayerState playerState)
    {
        if (_playerState == playerState)
        {
            QueueRefresh();
            return;
        }

        CleanupSubscriptions();

        _playerState = playerState;
        _player = playerState.Player;
        _topContainer = playerState.GetNodeOrNull<HBoxContainer>("TopInfoContainer");
        _healthBar = playerState.GetNodeOrNull<NHealthBar>("%HealthBar");

        _baseMinSize = playerState.CustomMinimumSize;
        _headerHeight = ResolveHeaderHeight(playerState);

        SetupResourceBar();
        SetupCardTray();
        SubscribeGlobalEvents();

        if (CombatManager.Instance.IsInProgress)
        {
            BindCombatState();
        }

        QueueRefresh();
    }

    public override void _ExitTree()
    {
        CleanupSubscriptions();
        _trayPosTween?.Kill();

        if (_resourceBar != null && GodotObject.IsInstanceValid(_resourceBar))
        {
            _resourceBar.LayoutWidthChanged -= OnResourceBarWidthChanged;
            _resourceBar.Resized -= OnResourceBarWidthChanged;
            _resourceBar.QueueFree();
        }
        _resourceBar = null;

        if (_cardTray != null && GodotObject.IsInstanceValid(_cardTray))
        {
            _cardTray.TrayHeightChanged -= OnTrayHeightChanged;
            _cardTray.AnyCardHoverChanged -= OnAnyCardHoverChanged;
            _cardTray.QueueFree();
        }
        _cardTray = null;

        if (_playerState != null && GodotObject.IsInstanceValid(_playerState))
        {
            _playerState.ZIndex = 0;
            _playerState.CustomMinimumSize = _baseMinSize;
        }

        base._ExitTree();
    }

    private void SetupResourceBar()
    {
        if (_playerState == null || _player == null)
        {
            return;
        }

        // Do not display gold/potions for the local player; only teammates
        if (LocalContext.IsMe(_player))
        {
            return;
        }

        _resourceBar = new TeammateResourceBar
        {
            Name = "CardReminderResourceBar"
        };
        _resourceBar.LayoutWidthChanged += OnResourceBarWidthChanged;
        _resourceBar.Resized += OnResourceBarWidthChanged;

        if (_topContainer != null && GodotObject.IsInstanceValid(_topContainer))
        {
            _topContainer.AddChild(_resourceBar);
            _topContainer.Resized += OnTopContainerResized;
        }
        else
        {
            _playerState.AddChild(_resourceBar);
            _resourceBar.Position = new Vector2(180f, 4f);
        }

        _resourceBar.Bind(_player);
    }

    private void SetupCardTray()
    {
        if (_playerState == null)
        {
            return;
        }

        _cardTray = new TeammateCardTray
        {
            Name = "CardReminderCardTray",
            Visible = false,
            ZIndex = 0
        };
        _cardTray.TrayHeightChanged += OnTrayHeightChanged;
        _cardTray.AnyCardHoverChanged += OnAnyCardHoverChanged;

        // Add at the end of playerState so cards sit cleanly above Hitbox and receive hover reliably.
        _playerState.AddChild(_cardTray);
        PositionCardTray();
    }

    private void SubscribeGlobalEvents()
    {
        CombatManager.Instance.CombatSetUp += OnCombatSetUp;
        CombatManager.Instance.CombatEnded += OnCombatEnded;
        CombatManager.Instance.TurnStarted += OnTurnStarted;
        CombatManager.Instance.PlayerEndedTurn += OnPlayerTurnReadyChanged;
        CombatManager.Instance.PlayerUnendedTurn += OnPlayerTurnReadyChanged;
        CombatManager.Instance.StateTracker.CombatStateChanged += OnCombatStateChanged;
        CardReminderSettings.SettingsChanged += OnSettingsChanged;
    }

    private void CleanupSubscriptions()
    {
        CombatManager.Instance.CombatSetUp -= OnCombatSetUp;
        CombatManager.Instance.CombatEnded -= OnCombatEnded;
        CombatManager.Instance.TurnStarted -= OnTurnStarted;
        CombatManager.Instance.PlayerEndedTurn -= OnPlayerTurnReadyChanged;
        CombatManager.Instance.PlayerUnendedTurn -= OnPlayerTurnReadyChanged;
        CombatManager.Instance.StateTracker.CombatStateChanged -= OnCombatStateChanged;
        CardReminderSettings.SettingsChanged -= OnSettingsChanged;

        if (_topContainer != null && GodotObject.IsInstanceValid(_topContainer))
        {
            _topContainer.Resized -= OnTopContainerResized;
        }

        if (_resourceBar != null && GodotObject.IsInstanceValid(_resourceBar))
        {
            _resourceBar.LayoutWidthChanged -= OnResourceBarWidthChanged;
            _resourceBar.Resized -= OnResourceBarWidthChanged;
        }

        UnbindCombatState();
    }

    private void BindCombatState()
    {
        UnbindCombatState();

        PlayerCombatState? pcs = _player?.PlayerCombatState;
        if (pcs == null)
        {
            return;
        }

        _subscribedCombatState = pcs;
        pcs.Hand.CardAdded += OnHandCardChanged;
        pcs.Hand.CardRemoved += OnHandCardChanged;
        pcs.Hand.ContentsChanged += OnHandContentsChanged;
        pcs.EnergyChanged += OnResourceChanged;
        pcs.StarsChanged += OnResourceChanged;
    }

    private void UnbindCombatState()
    {
        if (_subscribedCombatState == null)
        {
            return;
        }

        _subscribedCombatState.Hand.CardAdded -= OnHandCardChanged;
        _subscribedCombatState.Hand.CardRemoved -= OnHandCardChanged;
        _subscribedCombatState.Hand.ContentsChanged -= OnHandContentsChanged;
        _subscribedCombatState.EnergyChanged -= OnResourceChanged;
        _subscribedCombatState.StarsChanged -= OnResourceChanged;
        _subscribedCombatState = null;
    }

    private void OnTopContainerResized()
    {
        if (GodotObject.IsInstanceValid(this) && IsInsideTree())
        {
            CallDeferred(nameof(PositionCardTray));
        }
    }

    private void OnResourceBarWidthChanged()
    {
        if (GodotObject.IsInstanceValid(this) && IsInsideTree())
        {
            PositionCardTray();
            CallDeferred(nameof(PositionCardTray));
        }
    }

    private void OnPlayerTurnReadyChanged(Player player)
    {
        if (player == _player)
        {
            QueueRefresh();
        }
    }

    private void OnPlayerTurnReadyChanged(Player player, bool _)
    {
        OnPlayerTurnReadyChanged(player);
    }

    private void OnCombatSetUp(CombatState _)
    {
        BindCombatState();
        QueueRefresh();
    }

    private void OnCombatEnded(CombatRoom _)
    {
        UnbindCombatState();
        QueueRefresh();
    }

    private void OnTurnStarted(CombatState _)
    {
        QueueRefresh();
    }

    private void OnCombatStateChanged(CombatState _)
    {
        if (_subscribedCombatState == null && CombatManager.Instance.IsInProgress)
        {
            BindCombatState();
        }

        QueueRefresh();
    }

    private void OnHandCardChanged(CardModel _)
    {
        QueueRefresh();
    }

    private void OnHandContentsChanged()
    {
        QueueRefresh();
    }

    private void OnResourceChanged(int _, int __)
    {
        QueueRefresh();
    }

    private void OnSettingsChanged()
    {
        if (!GodotObject.IsInstanceValid(this) || !IsInsideTree())
        {
            return;
        }

        _resourceBar?.RefreshAll();
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_refreshQueued || !GodotObject.IsInstanceValid(this) || !IsInsideTree())
        {
            return;
        }

        _refreshQueued = true;
        CallDeferred(nameof(PerformDeferredRefresh));
    }

    private void PerformDeferredRefresh()
    {
        _refreshQueued = false;

        if (!GodotObject.IsInstanceValid(this) || !IsInsideTree() || _playerState == null || !GodotObject.IsInstanceValid(_playerState) || _player == null)
        {
            return;
        }

        _resourceBar?.RefreshAll();

        bool isLocalPlayer = LocalContext.IsMe(_player);
        bool shouldShowCards = CombatManager.Instance.IsInProgress
            && CardReminderSettings.ShowTeammateHandCards
            && (!isLocalPlayer || CardReminderSettings.ShowOwnHandCards);

        if (shouldShowCards && _subscribedCombatState == null)
        {
            BindCombatState();
        }

        float measuredHeader = ResolveHeaderHeight(_playerState);
        if (measuredHeader > 30f && (_cardTray == null || !_cardTray.Visible))
        {
            _headerHeight = measuredHeader;
        }

        PositionCardTray();
        _cardTray?.UpdateCards(_player, shouldShowCards);
        _cardTray?.RefreshCardVisuals();
        ApplyPlayerStateHeight(shouldShowCards);
    }

    private void PositionCardTray()
    {
        if (_cardTray == null || !GodotObject.IsInstanceValid(_cardTray) || _playerState == null || !GodotObject.IsInstanceValid(_playerState))
        {
            return;
        }

        float rightEdge = ResolvePlayerInfoRightEdge(_playerState);
        float targetX = rightEdge + CardTrayGapRight;
        if (_cardTray.Visible && _cardTray.Position.X > 10f && MathF.Abs(_cardTray.Position.X - targetX) > 2f)
        {
            _trayPosTween?.Kill();
            _trayPosTween = _cardTray.CreateTween();
            _trayPosTween.TweenProperty(_cardTray, "position:x", targetX, 0.18)
                .SetTrans(Tween.TransitionType.Cubic)
                .SetEase(Tween.EaseType.Out);
        }
        else
        {
            _trayPosTween?.Kill();
            _cardTray.Position = new Vector2(targetX, 0f);
        }
    }

    private void OnTrayHeightChanged(float _)
    {
        if (_player == null)
        {
            return;
        }

        bool isLocalPlayer = LocalContext.IsMe(_player);
        bool shouldShowCards = CombatManager.Instance.IsInProgress
            && CardReminderSettings.ShowTeammateHandCards
            && (!isLocalPlayer || CardReminderSettings.ShowOwnHandCards);

        ApplyPlayerStateHeight(shouldShowCards);
    }

    private void ApplyPlayerStateHeight(bool inCombatWithCardsEnabled)
    {
        if (_playerState == null || !GodotObject.IsInstanceValid(_playerState))
        {
            return;
        }

        float trayHeight = _cardTray?.CurrentTrayHeight ?? 0f;
        float singleRowHeight = NCard.defaultSize.Y * CardReminderSettings.CardScale + 6f;

        // Keep at least single-row height stable throughout combat so rows never jump when hand empties at turn end
        float desiredHeight = inCombatWithCardsEnabled
            ? Math.Max(_baseMinSize.Y, Math.Max(HeaderHeight, Math.Max(singleRowHeight, trayHeight) + 6f))
            : _baseMinSize.Y;

        Vector2 newMin = new(_baseMinSize.X, desiredHeight);
        if (!_playerState.CustomMinimumSize.IsEqualApprox(newMin))
        {
            _playerState.CustomMinimumSize = newMin;
        }

        ClampHitboxToHeader(_playerState);
    }

    public void ClampHitboxToHeader(NMultiplayerPlayerState playerState)
    {
        if (!GodotObject.IsInstanceValid(playerState))
        {
            return;
        }

        if (playerState.Hitbox != null && GodotObject.IsInstanceValid(playerState.Hitbox))
        {
            Vector2 hitboxSize = playerState.Hitbox.Size;
            float maxHitboxWidth = ResolvePlayerInfoRightEdge(playerState) + 6f;
            float clampedWidth = hitboxSize.X > maxHitboxWidth ? maxHitboxWidth : hitboxSize.X;
            float clampedHeight = hitboxSize.Y > HeaderHeight + 2f ? HeaderHeight : hitboxSize.Y;
            if (!Mathf.IsEqualApprox(clampedWidth, hitboxSize.X) || !Mathf.IsEqualApprox(clampedHeight, hitboxSize.Y))
            {
                playerState.Hitbox.SetDeferred(Control.PropertyName.Size, new Vector2(clampedWidth, clampedHeight));
            }
        }
    }

    private void OnAnyCardHoverChanged(bool hovered)
    {
        _isCardHovered = hovered;
        if (_playerState != null && GodotObject.IsInstanceValid(_playerState))
        {
            _playerState.ZIndex = _isCardHovered ? 5 : 0;
        }
    }

    private float ResolvePlayerInfoRightEdge(NMultiplayerPlayerState playerState)
    {
        float maxRight = 190f;

        if (_topContainer != null && GodotObject.IsInstanceValid(_topContainer) && _topContainer.IsInsideTree())
        {
            foreach (Control child in _topContainer.GetChildren().OfType<Control>())
            {
                if (!child.Visible)
                {
                    continue;
                }

                float width = ReferenceEquals(child, _resourceBar)
                    ? _resourceBar.EstimateRequiredWidth()
                    : Math.Max(child.Size.X, child.GetCombinedMinimumSize().X);
                if (width <= 0f)
                {
                    continue;
                }

                float right = child.GlobalPosition.X - playerState.GlobalPosition.X + width;
                if (right > maxRight && right < 640f)
                {
                    maxRight = right;
                }
            }
        }

        if (_resourceBar != null && GodotObject.IsInstanceValid(_resourceBar) && _resourceBar.Visible && _resourceBar.IsInsideTree())
        {
            float barRight = _resourceBar.GlobalPosition.X - playerState.GlobalPosition.X + _resourceBar.EstimateRequiredWidth();
            if (barRight > maxRight && barRight < 640f)
            {
                maxRight = barRight;
            }
        }

        Control? hpBarBox = _healthBar?.HpBarContainer ?? _healthBar;
        if (hpBarBox != null && GodotObject.IsInstanceValid(hpBarBox) && hpBarBox.IsInsideTree())
        {
            float hpRight = hpBarBox.GlobalPosition.X - playerState.GlobalPosition.X + hpBarBox.Size.X;
            if (hpRight > maxRight && hpRight < 640f)
            {
                maxRight = hpRight;
            }
        }

        return maxRight;
    }

    private float ResolveHeaderHeight(NMultiplayerPlayerState playerState)
    {
        float maxBottom = 0f;

        if (_healthBar != null && GodotObject.IsInstanceValid(_healthBar) && _healthBar.IsInsideTree())
        {
            float hbBottom = _healthBar.GlobalPosition.Y - playerState.GlobalPosition.Y + _healthBar.Size.Y;
            if (hbBottom > 24f && hbBottom < 140f)
            {
                maxBottom = Math.Max(maxBottom, hbBottom);
            }
        }

        if (_topContainer != null && GodotObject.IsInstanceValid(_topContainer) && _topContainer.IsInsideTree())
        {
            float topBottom = _topContainer.GlobalPosition.Y - playerState.GlobalPosition.Y + _topContainer.Size.Y;
            if (topBottom > 24f && topBottom < 140f)
            {
                maxBottom = Math.Max(maxBottom, topBottom);
            }
        }

        if (maxBottom > 24f)
        {
            return maxBottom + 4f;
        }

        if (_baseMinSize.Y > 24f && _baseMinSize.Y < 140f)
        {
            return _baseMinSize.Y;
        }

        return 64f;
    }
}
