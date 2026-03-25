using Godot;

/// <summary>
/// Rough battle prototype — cycles through Standard → Slow → Bouncing prompt types in order,
/// repeating from the beginning after all three have played.
/// Player HP decreases on miss. Press Space (battle_confirm) to hit prompts.
/// </summary>
public partial class BattleTest : Node2D
{
    private static readonly TimingPrompt.PromptType[] PromptCycle =
    {
        TimingPrompt.PromptType.Standard,
        TimingPrompt.PromptType.Slow,
        TimingPrompt.PromptType.Bouncing,
    };

    private ColorRect _playerHPFill;
    private ColorRect _enemyHPFill;

    private float _playerHP = 1.0f;
    private float _enemyHP  = 1.0f;

    private TimingPrompt _activePrompt;
    private bool  _waitingToSpawn = false;
    private float _spawnCooldown  = 0f;
    private int   _cycleIndex     = 1;  // scene-instanced prompt is Standard; next dynamic spawn starts at Slow

    private PackedScene _timingPromptScene;

    public override void _Ready()
    {
        _timingPromptScene = GD.Load<PackedScene>("res://Scenes/Battle/TimingPrompt.tscn");

        _playerHPFill = GetNode<ColorRect>("PlayerHP/Fill");
        _enemyHPFill  = GetNode<ColorRect>("EnemyHP/Fill");

        // Wire up the scene-instanced TimingPrompt — first attack starts immediately.
        _activePrompt = GetNode<TimingPrompt>("TimingPrompt");
        // AutoLoop defaults to true in TimingPrompt; disable it so the prompt does not
        // auto-restart after completing and re-emit PromptCompleted indefinitely.
        _activePrompt.AutoLoop = false;
        _activePrompt.PromptCompleted += OnPromptCompleted;
        GD.Print($"[BattleTest] Scene-instanced prompt ready (Standard). AutoLoop disabled.");
    }

    public override void _Process(double delta)
    {
        if (_waitingToSpawn)
        {
            _spawnCooldown -= (float)delta;
            if (_spawnCooldown <= 0f)
            {
                _waitingToSpawn = false;
                GD.Print("[BattleTest] Cooldown expired — freeing resolved prompt and spawning next.");
                // Free the resolved prompt now that the cooldown is over.
                if (_activePrompt != null && IsInstanceValid(_activePrompt))
                {
                    _activePrompt.QueueFree();
                    _activePrompt = null;
                }
                SpawnPrompt();
            }
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (@event.IsActionPressed("battle_confirm"))
            TimingPrompt.ConfirmAll();
    }

    // -------------------------------------------------------------------------

    private void SpawnPrompt()
    {
        // Guard: should never be called while a prompt is still active, but defend
        // against any edge-case double-call that could cause overlapping prompts.
        if (_activePrompt != null && IsInstanceValid(_activePrompt))
        {
            GD.PrintErr("[BattleTest] SpawnPrompt called while _activePrompt is still valid — skipping.");
            return;
        }

        var type = PromptCycle[_cycleIndex];
        _cycleIndex = (_cycleIndex + 1) % PromptCycle.Length;
        GD.Print($"[BattleTest] SpawnPrompt — spawning {type} (next cycleIndex={_cycleIndex}).");

        var prompt = _timingPromptScene.Instantiate<TimingPrompt>();
        prompt.Type     = type;
        prompt.AutoLoop = false;  // prevent auto-restart and repeated PromptCompleted emissions
        prompt.PromptCompleted += OnPromptCompleted;  // connect before AddChild so signal is live when _Ready fires
        AddChild(prompt);
        _activePrompt = prompt;
    }

    private void OnPromptCompleted(int result)
    {
        // Guard: detect if this fires more than once for the same prompt cycle.
        if (_waitingToSpawn)
        {
            GD.PrintErr($"[BattleTest] OnPromptCompleted fired while already waiting to spawn — " +
                        $"possible double-fire or AutoLoop still active. result={result}");
            return;
        }

        GD.Print($"[BattleTest] OnPromptCompleted — result={(TimingPrompt.InputResult)result}. " +
                 $"Starting 3s cooldown.");

        // Do NOT free the prompt yet — when resolved, _showMovingRing = false but
        // the target ring and hit-window band are still drawn unconditionally, so
        // the prompt stays visible as a static indicator during the cooldown.
        // SpawnPrompt() will QueueFree it when the 3-second wait expires.
        if ((TimingPrompt.InputResult)result == TimingPrompt.InputResult.Miss)
        {
            _playerHP = Mathf.Max(0f, _playerHP - 0.2f);
            UpdateHPBars();
        }

        _waitingToSpawn = true;
        _spawnCooldown  = 3.0f;
    }

    private void UpdateHPBars()
    {
        _playerHPFill.Size = new Vector2(300f * _playerHP, _playerHPFill.Size.Y);
    }
}
