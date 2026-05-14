using System;
using Godot;

/// <summary>
/// Narrative dialogue component — multi-line speaker-tagged character speech with
/// character-by-character reveal and player-skippable advancement.
///
/// Distinct from BattleMessage (which is a non-skippable duration-based system
/// notification during mechanical events). See CLAUDE.md "Text UI Systems".
///
/// Lifecycle:
///   PlayDialogue(lines) → fade panel in → per-line reveal+wait → fade panel out.
///   FadeOutStarted emitted when the final line's advance begins the fade-out tween
///   (caller can kick off simultaneous cross-fades, e.g. music fade-in).
///   DialogueCompleted emitted after fade-out plus PostDialogueBufferSec has elapsed.
///
/// Input:
///   Listens for "battle_confirm" (Space / gamepad button 0, per project.godot).
///   During reveal: completes the current line instantly.
///   After reveal: advances to the next line, skipping the remaining auto-advance.
///   Consumes the input event via SetInputAsHandled so it does not bleed into
///   BattleTest._Input. Callers should still set _inputLocked during dialogue as
///   a secondary guard.
/// </summary>
public partial class BattleDialogue : Node
{
    public struct DialogueLine
    {
        public string Speaker;
        public string Text;
        public float  AutoAdvanceSeconds;  // seconds to wait after full reveal before auto-advancing
        public float  RevealSpeed;         // characters per second; 0 = use DefaultRevealSpeed
    }

    [Signal] public delegate void DialogueCompletedEventHandler();
    [Signal] public delegate void FadeOutStartedEventHandler();

    // Speaker-based name-tag tints. Body uses BodyColor regardless of speaker —
    // only the name tag carries the speaker styling (asymmetry is intentional).
    [Export] public Color ApprenticeNameColor   = new Color(0.96f, 0.93f, 0.82f, 1f); // parchment / off-white
    [Export] public Color HarbingerNameColor    = new Color(0.65f, 0.72f, 0.82f, 1f); // cold blue-grey
    [Export] public Color BodyColor             = new Color(0.96f, 0.93f, 0.82f, 1f); // parchment / off-white

    [Export] public float PanelFadeInSec        = 0.5f;
    [Export] public float PanelFadeOutSec       = 0.5f;
    [Export] public float DefaultRevealSpeed    = 40f;  // characters per second
    [Export] public float PostDialogueBufferSec = 0.15f; // pause after fade-out before DialogueCompleted; prevents input bleed

    private const float PanelHeight       = 120f;
    // Bottom inset clears the player panel strip at the bottom-center (Phase 6 C6
    // moved it from the bottom-left corner to a centered row). Shared constant on
    // BattleTest so any future strip-height change flows here automatically.
    private const float PanelBottomInset  = BattleTest.OverlayBottomInset;
    private const float PanelAnchorLeft   = 0.2f;  // horizontal anchor — 60% width, centered (0.2..0.8)
    private const float PanelAnchorRight  = 0.8f;
    private const float NameTagWidth      = 200f;
    private const float NameTagHeight     = 36f;
    private const float NameTagOverlap    = 10f;  // how far the name tag sits below the panel's top edge
    private const float NameTagInsetLeft  = 28f;  // from the panel's left edge
    private const int   BodyFontSize      = 20;
    private const int   NameTagFontSize   = 18;

    private CanvasLayer    _layer;
    private Control        _root;          // fades as a whole via modulate:a
    private PanelContainer _panel;
    private Label          _bodyLabel;
    private Label          _nameTagLabel;

    private Tween _fadeTween;

    // Playback state
    private DialogueLine[] _lines;
    private int            _lineIndex;
    private double         _revealAccumulator;   // fractional characters accumulated
    private int            _revealedChars;
    private bool           _lineFullyRevealed;
    private double         _autoAdvanceRemaining;
    private bool           _playing;             // true from PlayDialogue entry to DialogueCompleted emit
    private bool           _awaitingInput;       // true only while a line is actively revealing or waiting for advance

    public override void _Ready()
    {
        BuildUi();
    }

    private void BuildUi()
    {
        _layer       = new CanvasLayer();
        _layer.Name  = "BattleDialogueLayer";
        _layer.Layer = 10;  // above status panels, menu, same tier as BattleMessage
        AddChild(_layer);

        _root = new Control();
        _root.Name        = "BattleDialogueRoot";
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.MouseFilter = Control.MouseFilterEnum.Ignore;
        _root.Modulate    = new Color(1f, 1f, 1f, 0f);  // start fully transparent
        _layer.AddChild(_root);

        // Dialogue panel — 60% of viewport width, centered horizontally, anchored to bottom.
        // Uses percentage anchors so the panel scales with viewport size without relying on
        // post-layout resize handlers.
        _panel = BattleTest.MakeLayeredPanel(minWidth: 400f, out var bodyContent, minHeight: PanelHeight);
        _panel.AnchorLeft     = PanelAnchorLeft;
        _panel.AnchorRight    = PanelAnchorRight;
        _panel.AnchorTop      = 1f;
        _panel.AnchorBottom   = 1f;
        _panel.GrowHorizontal = Control.GrowDirection.Both;
        _panel.GrowVertical   = Control.GrowDirection.Begin;
        _panel.OffsetLeft     = 0f;
        _panel.OffsetRight    = 0f;
        _panel.OffsetTop      = -(PanelBottomInset + PanelHeight);
        _panel.OffsetBottom   = -PanelBottomInset;
        _panel.MouseFilter    = Control.MouseFilterEnum.Ignore;
        _root.AddChild(_panel);

        _bodyLabel = new Label();
        _bodyLabel.AutowrapMode         = TextServer.AutowrapMode.WordSmart;
        _bodyLabel.HorizontalAlignment  = HorizontalAlignment.Left;
        _bodyLabel.VerticalAlignment    = VerticalAlignment.Center;
        _bodyLabel.SizeFlagsHorizontal  = Control.SizeFlags.Fill;
        _bodyLabel.SizeFlagsVertical    = Control.SizeFlags.ExpandFill;
        _bodyLabel.VisibleCharacters    = 0;
        BattleTest.StyleLabel(_bodyLabel, fontSize: BodyFontSize);
        _bodyLabel.AddThemeColorOverride("font_color", BodyColor);
        bodyContent.AddChild(_bodyLabel);

        // Name-tag label — floating sibling positioned above-left of the panel, with a
        // slight downward overlap so its lower edge reads as crossing the panel's top border.
        // Anchored to the same horizontal position as the panel's left edge (PanelAnchorLeft)
        // so it stays aligned with the panel as viewport width scales.
        _nameTagLabel = new Label();
        _nameTagLabel.HorizontalAlignment = HorizontalAlignment.Left;
        _nameTagLabel.VerticalAlignment   = VerticalAlignment.Center;
        _nameTagLabel.AnchorLeft          = PanelAnchorLeft;
        _nameTagLabel.AnchorRight         = PanelAnchorLeft;
        _nameTagLabel.AnchorTop           = 1f;
        _nameTagLabel.AnchorBottom        = 1f;
        _nameTagLabel.GrowHorizontal      = Control.GrowDirection.End;
        _nameTagLabel.GrowVertical        = Control.GrowDirection.Begin;
        _nameTagLabel.OffsetLeft          = NameTagInsetLeft;
        _nameTagLabel.OffsetRight         = NameTagInsetLeft + NameTagWidth;
        _nameTagLabel.OffsetTop           = -(PanelBottomInset + PanelHeight + NameTagHeight - NameTagOverlap);
        _nameTagLabel.OffsetBottom        = -(PanelBottomInset + PanelHeight - NameTagOverlap);
        _nameTagLabel.MouseFilter         = Control.MouseFilterEnum.Ignore;
        BattleTest.StyleLabel(_nameTagLabel, fontSize: NameTagFontSize);
        _root.AddChild(_nameTagLabel);
    }

    /// <summary>
    /// Starts the dialogue sequence. Caller connects to FadeOutStarted and DialogueCompleted
    /// signals to coordinate post-dialogue transitions (music fade-in, menu show, etc.).
    /// No-op with an error log if called while already playing.
    /// </summary>
    public void PlayDialogue(DialogueLine[] lines)
    {
        if (_playing)
        {
            GD.PrintErr("[BattleDialogue] PlayDialogue called while already playing — ignoring.");
            return;
        }
        if (lines == null || lines.Length == 0)
        {
            GD.PrintErr("[BattleDialogue] PlayDialogue called with null or empty lines — emitting completion signals immediately.");
            EmitSignal(SignalName.FadeOutStarted);
            EmitSignal(SignalName.DialogueCompleted);
            return;
        }

        _lines         = lines;
        _lineIndex     = -1;
        _playing       = true;
        _awaitingInput = false;

        FadeInPanel();
    }

    private void FadeInPanel()
    {
        _fadeTween?.Kill();
        _fadeTween = CreateTween();
        _fadeTween.TweenProperty(_root, "modulate:a", 1f, PanelFadeInSec);
        _fadeTween.TweenCallback(Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(this)) return;
            StartNextLine();
        }));
    }

    private void StartNextLine()
    {
        _lineIndex++;
        if (_lineIndex >= _lines.Length)
        {
            BeginFadeOut();
            return;
        }

        var line = _lines[_lineIndex];

        _nameTagLabel.Text = line.Speaker ?? "";
        Color nameColor = line.Speaker == "The Harbinger" ? HarbingerNameColor : ApprenticeNameColor;
        _nameTagLabel.AddThemeColorOverride("font_color", nameColor);

        _bodyLabel.Text              = line.Text ?? "";
        _bodyLabel.VisibleCharacters = 0;

        _revealAccumulator    = 0.0;
        _revealedChars        = 0;
        _lineFullyRevealed    = (line.Text ?? "").Length == 0;
        _autoAdvanceRemaining = Math.Max(0f, line.AutoAdvanceSeconds);
        _awaitingInput        = true;
    }

    public override void _Process(double delta)
    {
        if (!_playing || !_awaitingInput) return;

        var line = _lines[_lineIndex];

        if (!_lineFullyRevealed)
        {
            float cps = line.RevealSpeed > 0f ? line.RevealSpeed : DefaultRevealSpeed;
            _revealAccumulator += delta * cps;
            int target = (int)_revealAccumulator;
            int total  = (line.Text ?? "").Length;
            if (target >= total)
            {
                target = total;
                _lineFullyRevealed = true;
            }
            if (target != _revealedChars)
            {
                _revealedChars = target;
                _bodyLabel.VisibleCharacters = _revealedChars;
            }
        }
        else
        {
            _autoAdvanceRemaining -= delta;
            if (_autoAdvanceRemaining <= 0.0)
                AdvanceLine();
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (!_playing || !_awaitingInput) return;
        if (!@event.IsActionPressed("battle_confirm")) return;

        GetViewport().SetInputAsHandled();

        if (!_lineFullyRevealed)
        {
            int total = (_lines[_lineIndex].Text ?? "").Length;
            _revealedChars               = total;
            _revealAccumulator           = total;
            _bodyLabel.VisibleCharacters = total;
            _lineFullyRevealed           = true;
        }
        else
        {
            AdvanceLine();
        }
    }

    private void AdvanceLine()
    {
        _awaitingInput = false;
        StartNextLine();
    }

    private void BeginFadeOut()
    {
        EmitSignal(SignalName.FadeOutStarted);

        _fadeTween?.Kill();
        _fadeTween = CreateTween();
        _fadeTween.TweenProperty(_root, "modulate:a", 0f, PanelFadeOutSec);
        _fadeTween.TweenInterval(PostDialogueBufferSec);
        _fadeTween.TweenCallback(Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(this)) return;
            _playing = false;
            EmitSignal(SignalName.DialogueCompleted);
        }));
    }
}
