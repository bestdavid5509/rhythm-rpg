using System;
using Godot;

/// <summary>
/// Partial — sprite frame construction, animation callbacks, dead-flag guards,
/// and end-of-battle overlay for BattleTest.
/// All members declared here are part of the same BattleTest class; fields and
/// methods defined in BattleTest.cs and BattleMenu.cs are fully accessible.
/// </summary>
public partial class BattleTest : Node2D
{
    // =========================================================================
    // Player sprite setup
    // =========================================================================

    /// <summary>
    /// Builds and assigns SpriteFrames for the player AnimatedSprite2D.
    /// Each animation is a separate horizontal-strip PNG in Assets/Characters/Knight/.
    /// Returns the frame height so the caller can position the sprite floor-anchored.
    /// </summary>
    private int BuildPlayerSpriteFrames()
    {
        const string Base = "res://Assets/Characters/Knight/";
        const int    Fw   = 120;   // all knight frames are 120×80 (not square)
        const int    Fh   = 80;
        var frames = new SpriteFrames();
        if (frames.HasAnimation("default")) frames.RemoveAnimation("default");

        // idle / run / hit / death — each file maps to one animation.
        //
        // All player attack animations come from _AttackComboNoMovement.png (1200×80, 10 frames):
        //   "combo"        — all 10 frames; used as the frame-index reference for wind-up holds
        //   "combo_slash1" — frames 1–3; first strike (single attack resolve, combo passes 0 & 1)
        //   "combo_slash2" — frames 6–9; second strike (combo final pass)
        //
        // _Attack2NoMovement.png (720×80, 6 frames) provides one animation:
        //   "parry" — frames 2–5; plays on every successful enemy-attack parry
        //
        // _AttackNoMovement.png (480×80, 4 frames) provides one animation:
        //   "attack1" — frames 0–3; used for the perfect parry counter swing
        const string Combo = Base + "_AttackComboNoMovement.png";
        int frameH = AddPlayerAnimation(frames, "idle",         Base + "_Idle.png",              loop: true,  fw: Fw, fh: Fh);
                     AddPlayerAnimation(frames, "run",          Base + "_Run.png",               loop: true,  fw: Fw, fh: Fh);
                     AddPlayerAnimation(frames, "combo",        Combo,                           loop: false, fw: Fw, fh: Fh);
                     AddPlayerAnimation(frames, "combo_slash1", Combo,                           loop: false, fw: Fw, fh: Fh, startFrame: 1, endFrame: 3);
                     AddPlayerAnimation(frames, "combo_slash2", Combo,                           loop: false, fw: Fw, fh: Fh, startFrame: 6, endFrame: 9);
                     AddPlayerAnimation(frames, "parry",        Base + "_Attack2NoMovement.png", loop: false, fw: Fw, fh: Fh, startFrame: 2, endFrame: 5);
                     AddPlayerAnimation(frames, "attack1",      Base + "_AttackNoMovement.png",  loop: false, fw: Fw, fh: Fh);
                     AddPlayerAnimation(frames, "cast",            Base + "_CrouchAttack.png",    loop: false, fw: Fw, fh: Fh, startFrame: 0, endFrame: 3);
                     AddPlayerAnimation(frames, "cast_transition", Base + "_CrouchTransition.png", loop: false, fw: Fw, fh: Fh, startFrame: 0, endFrame: 0);
                     AddPlayerAnimation(frames, "hit",          Base + "_Hit.png",               loop: false, fw: Fw, fh: Fh);
                     AddPlayerAnimation(frames, "death",        Base + "_DeathNoMovement.png",   loop: false, fw: Fw, fh: Fh);

        // Dedicated item-use strip (9 frames).
        AddPlayerAnimation(frames, "item_use", Base + "_ItemUse.png", loop: false, fw: Fw, fh: Fh);

        _playerAnimSprite.SpriteFrames = frames;

        // Diagnostic: confirm every animation and its actual frame count.
        // A 0-frame entry means the PNG failed to load (missing .import file).
        // Calling Play() on a 0-frame animation hides the sprite — the primary blink cause.
        GD.Print("[BattleTest] Player sprite frames summary:");
        foreach (string anim in frames.GetAnimationNames())
            GD.Print($"[BattleTest]   {anim}: {frames.GetFrameCount(anim)} frame(s)");

        return frameH > 0 ? frameH : Fh;
    }

    /// <summary>
    /// Loads a horizontal-strip texture and adds it as a named animation.
    /// All knight PNGs use 120×80 frames — pass fw=120, fh=80 explicitly.
    ///
    /// <paramref name="startFrame"/> skips leading sheet frames (zero-based).
    /// <paramref name="endFrame"/> is the last sheet frame to include (inclusive).
    ///   Pass -1 (default) to include all frames from startFrame to the end of the strip.
    ///
    /// Examples (all from _AttackComboNoMovement.png, 1200×80 = 10 frames):
    ///   "combo"        startFrame=0  endFrame=-1 → frames 0–9  (all 10)
    ///   "combo_slash1" startFrame=1  endFrame=3  → frames 1–3  (3 frames, first strike)
    ///   "combo_slash2" startFrame=6  endFrame=9  → frames 6–9  (4 frames, second strike)
    ///   "parry"        startFrame=2  endFrame=5  → frames 2–5  (4 frames, defensive deflect)
    ///
    /// Returns the frame height for floor-anchored Y positioning; returns 0 on load failure.
    /// </summary>
    private static int AddPlayerAnimation(
        SpriteFrames frames, string name, string path,
        bool loop, int fw, int fh, float fps = 12f, int startFrame = 0, int endFrame = -1)
    {
        var texture = GD.Load<Texture2D>(path);
        if (texture == null)
        {
            // Most likely cause: PNG has no .import file yet.
            // Open the Godot editor once to trigger auto-import, then re-run.
            GD.PrintErr($"[BattleTest] LOAD FAILED — '{name}' will have 0 frames: {path}");
            frames.AddAnimation(name);  // stub so Play() won't throw; sprite will hide until fixed
            return 0;
        }

        int stripCount = texture.GetWidth() / fw;
        int last       = (endFrame < 0) ? stripCount - 1 : endFrame;
        int used       = last - startFrame + 1;

        GD.Print($"[BattleTest]   '{name}': {texture.GetWidth()}x{texture.GetHeight()}  " +
                 $"fw={fw} fh={fh}  strip={stripCount}  frames={startFrame}–{last}  used={used}");

        frames.AddAnimation(name);
        frames.SetAnimationSpeed(name, fps);
        frames.SetAnimationLoop(name, loop);

        for (int i = startFrame; i <= last; i++)
        {
            var atlas    = new AtlasTexture();
            atlas.Atlas  = texture;
            atlas.Region = new Rect2(i * fw, 0, fw, fh);
            frames.AddFrame(name, atlas);
        }

        return fh;
    }

    /// <summary>
    /// Adds an animation sampled from a list of (texture path, frame index) pairs.
    /// Supports non-contiguous frames AND frames pulled from multiple source strips.
    /// Kept as a utility for future custom multi-source animations (e.g. blending poses
    /// across strips). Not currently used — all active animations use AddPlayerAnimation.
    /// </summary>
    private static void AddPlayerAnimationMixed(
        SpriteFrames frames, string name, bool loop, int fw, int fh, float fps,
        params (string path, int frameIndex)[] framePicks)
    {
        frames.AddAnimation(name);
        frames.SetAnimationSpeed(name, fps);
        frames.SetAnimationLoop(name, loop);
        foreach (var (path, frameIdx) in framePicks)
        {
            var texture = GD.Load<Texture2D>(path);
            if (texture == null)
            {
                GD.PrintErr($"[BattleTest] LOAD FAILED for '{name}': {path}");
                continue;
            }
            var atlas    = new AtlasTexture();
            atlas.Atlas  = texture;
            atlas.Region = new Rect2(frameIdx * fw, 0, fw, fh);
            frames.AddFrame(name, atlas);
        }
    }

    // =========================================================================
    // Enemy sprite setup
    // =========================================================================

    /// <summary>
    /// Builds and assigns SpriteFrames for the enemy AnimatedSprite2D by slicing
    /// the enemy's sprite sheet into named animations. All layout values are read
    /// from EnemyData.AnimationConfig — no hardcoded row/frame constants.
    /// </summary>
    private void BuildEnemySpriteFrames()
    {
        GD.Print("[BattleTest] BuildEnemySpriteFrames called.");

        if (_enemyAnimSprite.SpriteFrames != null &&
            _enemyAnimSprite.SpriteFrames.HasAnimation("idle"))
        {
            GD.Print("[BattleTest] BuildEnemySpriteFrames — already built, skipping.");
            return;
        }

        if (EnemyData?.AnimationConfig == null || string.IsNullOrEmpty(EnemyData.SpritesheetPath))
        {
            GD.PrintErr("[BattleTest] BuildEnemySpriteFrames — EnemyData or AnimationConfig is null.");
            return;
        }

        var cfg = EnemyData.AnimationConfig;
        int Fw  = EnemyData.FrameWidth;
        int Fh  = EnemyData.FrameHeight;

        var texture = GD.Load<Texture2D>(EnemyData.SpritesheetPath);
        if (texture == null)
        {
            GD.PrintErr($"[BattleTest] Could not load enemy sprite sheet: {EnemyData.SpritesheetPath}");
            return;
        }

        var frames = new SpriteFrames();
        if (frames.HasAnimation("default")) frames.RemoveAnimation("default");

        AddEnemyAnimation(frames, texture, "idle",         row: cfg.IdleRow,        count: cfg.IdleFrames,        fw: Fw, fh: Fh, fps: 12f, loop: true);
        AddEnemyAnimation(frames, texture, "run",          row: cfg.RunRow,         count: cfg.RunFrames,         fw: Fw, fh: Fh, fps: 12f, loop: true);
        AddEnemyAnimation(frames, texture, "melee_attack", row: cfg.MeleeAttackRow, count: cfg.MeleeAttackFrames, fw: Fw, fh: Fh, fps: 12f, loop: false);
        if (cfg.LightAttackFrames > 0)
            AddEnemyAnimation(frames, texture, "light_attack", row: cfg.LightAttackRow, count: cfg.LightAttackFrames, fw: Fw, fh: Fh, fps: 12f, loop: false, startCol: cfg.LightAttackStartCol);
        AddEnemyAnimation(frames, texture, "cast_intro",   row: cfg.CastIntroRow,   count: cfg.CastIntroFrames,   fw: Fw, fh: Fh, fps: 12f, loop: false);
        if (cfg.CastLoopFrames > 0)
            AddEnemyAnimation(frames, texture, "cast_loop", row: cfg.CastLoopRow, count: cfg.CastLoopFrames, fw: Fw, fh: Fh, fps: 12f, loop: true, startCol: cfg.CastLoopStartCol);

        if (cfg.HasCastEnd)
            AddEnemyAnimation(frames, texture, "cast_end", row: cfg.CastEndRow, count: cfg.CastEndFrames, fw: Fw, fh: Fh, fps: 12f, loop: false, startCol: cfg.CastEndStartCol);

        AddEnemyAnimation(frames, texture, "death", row: cfg.DeathRow, count: cfg.DeathFrames, fw: Fw, fh: Fh, fps: cfg.DeathFps, loop: false);

        // Hurt animations — from separate sheet (hurt_flash + hurt_full) or main sheet (hurt).
        if (!string.IsNullOrEmpty(cfg.HurtSheetPath))
        {
            var hurtTexture = GD.Load<Texture2D>(cfg.HurtSheetPath);
            if (hurtTexture != null)
            {
                AddEnemyAnimation(frames, hurtTexture, "hurt_flash", row: 0, count: cfg.HurtFrames,     fw: Fw, fh: Fh, fps: 12f, loop: false);
                AddEnemyAnimation(frames, hurtTexture, "hurt_full",  row: 0, count: cfg.HurtFullFrames,  fw: Fw, fh: Fh, fps: 12f, loop: false);
            }
            else
                GD.PrintErr($"[BattleTest] Could not load enemy hurt sheet: {cfg.HurtSheetPath}");
        }
        else
        {
            AddEnemyAnimation(frames, texture, "hurt", row: cfg.HurtRow, count: cfg.HurtFrames, fw: Fw, fh: Fh, fps: 12f, loop: false);
        }

        // Append one fully-transparent frame at the end of the death animation,
        // held for ~0.5 s so the Victory label appears only after death particles dissipate.
        var blankImage   = Image.CreateEmpty(Fw, Fh, false, Image.Format.Rgba8);
        var blankTexture = ImageTexture.CreateFromImage(blankImage);
        frames.AddFrame("death", blankTexture, duration: 6.0f);  // 6.0 × (1/12 s) ≈ 0.5 s

        _enemyAnimSprite.SpriteFrames = frames;
        GD.Print($"[BattleTest] Enemy sprite frames built from {EnemyData.SpritesheetPath}.");
    }

    /// <param name="startCol">First column to read from (default 0). Use for sub-ranges of a row, e.g. cast_end starts at col 18.</param>
    private static void AddEnemyAnimation(
        SpriteFrames frames, Texture2D sheet,
        string name, int row, int count, int fw, int fh, float fps, bool loop,
        int startCol = 0)
    {
        frames.AddAnimation(name);
        frames.SetAnimationSpeed(name, fps);
        frames.SetAnimationLoop(name, loop);
        for (int col = startCol; col < startCol + count; col++)
        {
            var atlas    = new AtlasTexture();
            atlas.Atlas  = sheet;
            atlas.Region = new Rect2(col * fw, row * fh, fw, fh);
            frames.AddFrame(name, atlas);
        }
    }

    // -------------------------------------------------------------------------
    // Enemy cast animation handlers
    // -------------------------------------------------------------------------
    // Sequence: cast_intro (once) → cast_loop (until sequence ends) → cast_end (once) → idle
    // Each handler disconnects itself before transitioning to prevent stacking on repeat turns.

    private void OnCastIntroFinished()
    {
        GD.Print("[BattleTest] OnCastIntroFinished fired.");
        SafeDisconnectEnemyAnim(OnCastIntroFinished);
        if (_enemyDead) return;
        // If the enemy has no cast_loop animation (CastLoopFrames = 0), hold idle for
        // the remainder of the prompt sequence instead.
        int castLoopFrames = EnemyData?.AnimationConfig?.CastLoopFrames ?? 0;
        PlayEnemy(castLoopFrames > 0 ? "cast_loop" : "idle");
    }

    /// <summary>
    /// Fires when the enemy's melee "attack" animation completes during a hop-in turn.
    /// Sets _hopInAnimFinished and, if the sequence has already completed, hands off to
    /// ProceedAfterHopInAnim which applies PostAnimationDelayMs then calls PlayTeardown.
    /// </summary>
    private void OnEnemyAttackAnimFinished()
    {
        GD.Print("[BattleTest] OnEnemyAttackAnimFinished fired.");
        SafeDisconnectEnemyAnim(OnEnemyAttackAnimFinished);
        if (_enemyDead) return;  // death already in progress — don't interfere

        // Determine what to show after this animation finishes.
        bool parryCounterImminent = _parryClean && _hopInSequenceCompleted;
        if (parryCounterImminent)
        {
            // Parry counter is about to take ownership — don't play anything.
        }
        else if (_battleSystem.CurrentAttackIsHopIn)
        {
            // Check if there's a next step with a WaitAnimation to freeze on.
            int nextIdx = _battleSystem.GetLastStepRun() + 1;
            var steps   = _battleSystem.GetCurrentAttack()?.Steps;
            if (steps != null && nextIdx < steps.Count)
            {
                var nextStep = steps[nextIdx];
                if (!string.IsNullOrEmpty(nextStep.WaitAnimation))
                {
                    PlayEnemy(nextStep.WaitAnimation);
                    _enemyAnimSprite.Stop();
                    _enemyAnimSprite.Frame = 0;
                }
                else
                    PlayEnemy("idle");
            }
            else
                PlayEnemy("idle");  // Last step — return to idle.
        }
        else
        {
            PlayEnemy("idle");
        }

        _hopInAnimFinished = true;
        if (_hopInSequenceCompleted)
            ProceedAfterHopInAnim();
    }

    private void OnCastEndFinished()
    {
        GD.Print("[BattleTest] OnCastEndFinished fired.");
        SafeDisconnectEnemyAnim(OnCastEndFinished);
        PlayEnemy("idle");
    }

    // -------------------------------------------------------------------------
    // Player reaction handlers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fires when the player's cast animation completes during a magic attack turn.
    /// Shows the target zone and starts the BattleSystem sequence now that the wind-up
    /// has played. The sequence runs the timing circle and spawns the effect sprite.
    /// </summary>
    private void OnPlayerCastFinished()
    {
        SafeDisconnectPlayerAnim(OnPlayerCastFinished);
        if (_playerDead) return;

        // Godot 4 resets Frame to 0 when Stop() is called on a finished non-looping animation.
        // Re-apply the last frame index explicitly so the knight holds the sword-extended pose.
        StopPlayer();       // OWNER: OnPlayerCastFinished — hold last cast frame during spell sequence
        SetPlayerFrame(3);  // frame 3 = last frame of cast; must come after Stop() due to Godot 4 reset
        GetTree().CreateTimer(0.2f).Timeout += () =>
        {
            if (_playerDead) return;
            _targetZone.Position = _playerMagicPromptPos;
            _targetZone.Visible  = true;
            _battleSystem.SetAttack(_activeMagicAttack);
            _battleSystem.StartSequence(this, _playerMagicDefenderCenter, _playerMagicPromptPos, isPlayerAttack: true);
        };
    }

    /// <summary>
    /// Fires when cast_transition completes after a magic attack resolves.
    /// Returns the player to idle — the transition frame bridges the held cast pose and rest.
    /// </summary>
    private void OnPlayerCastTransitionFinished()
    {
        SafeDisconnectPlayerAnim(OnPlayerCastTransitionFinished);
        PlayPlayer("idle");  // OWNER: OnPlayerCastTransitionFinished — magic resolved, return to rest
    }

    private void OnParryFinished()
    {
        SafeDisconnectPlayerAnim(OnParryFinished);
        PlayPlayer("idle");  // OWNER: enemy pass resolved, parry complete
    }

    private void OnHitAnimFinished()
    {
        SafeDisconnectPlayerAnim(OnHitAnimFinished);
        PlayPlayer("idle");  // OWNER: enemy pass resolved, flinch complete
    }

    // -------------------------------------------------------------------------
    // Death handlers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fires when the player death animation completes.
    /// Does not return to idle — the sprite holds on the final death frame.
    /// </summary>
    private void OnPlayerDeathFinished()
    {
        SafeDisconnectPlayerAnim(OnPlayerDeathFinished);
        GD.Print("[BattleTest] Player died.");
        ShowEndLabel("Game Over");
    }

    /// <summary>
    /// Fires when the enemy death animation completes (from player attack or parry counter).
    /// Does not return to idle — the sprite holds on the final death frame.
    /// If Phase2EnemyData is assigned and the transition hasn't fired yet, the death
    /// completion is the handoff moment for the phase swap — the reveal sprite spawned
    /// earlier (at frame 4 of death) is already playing behind the now-transparent
    /// warrior sprite.
    /// </summary>
    private void OnEnemyDeathFinished()
    {
        SafeDisconnectEnemyAnim(OnEnemyDeathFinished);
        if (SkipPhaseTransition && IsPhaseTransitionPending())
        {
            GD.Print("[BattleTest] SkipPhaseTransition — swapping to Phase 2 immediately.");
            SwapToPhase2();
            return;
        }
        if (IsPhaseTransitionPending())
        {
            // Warrior death finished on the main sprite. The reveal sprite has been
            // playing concurrently (reveal frames → appended idle frames) and is
            // holding on its last idle frame by now. Swap to Phase 2 sprite + play
            // idle (frees the reveal), then run the dialogue and finalise.
            GD.Print("[BattleTest] Phase 1 death complete — applying Phase 2 sprite and queueing dialogue.");
            ApplyPhase2Sprite();
            GetTree().CreateTimer(0.5f).Timeout += () =>
            {
                ShowBattleMessage("You've only just begun to suffer.");
                StartPhase2Music();
                GetTree().CreateTimer(3.0f).Timeout += SwapToPhase2;
            };
            return;
        }
        GD.Print("[BattleTest] Enemy defeated.");
        GetTree().CreateTimer(1.0f).Timeout += () => ShowEndLabel("Victory!");
    }

    // =========================================================================
    // Phase 1 → Phase 2 transition
    // =========================================================================

    /// <summary>
    /// Gating predicate: transition is pending if a Phase 2 EnemyData is assigned,
    /// _phaseTransitionConsumed is still false (point-of-no-return not yet crossed),
    /// and the player is not also dead (simultaneous death falls through to the
    /// normal Game Over path).
    /// </summary>
    private bool IsPhaseTransitionPending()
        => Phase2EnemyData != null && !_phaseTransitionConsumed && !_playerDead;

    /// <summary>
    /// Called next to every site that starts the enemy death animation. Schedules the
    /// boss reveal sprite to spawn at frame 4 of death (4 / 12fps = 0.333s after play start).
    /// No-op unless the transition is pending and not skipped.
    /// </summary>
    private void ScheduleBossRevealIfPhase1()
    {
        if (!IsPhaseTransitionPending()) return;
        // Fade Phase 1 music over the death-to-reveal window. The silence that follows
        // holds through the boss reveal animation; Phase 2 music starts only when the
        // "You've only just begun to suffer." dialogue appears (below in OnEnemyDeathFinished).
        FadeOutMusic(2.5f);
        if (SkipPhaseTransition) return;  // SwapToPhase2 runs directly in OnEnemyDeathFinished
        GetTree().CreateTimer(4f / 12f).Timeout += SpawnBossReveal;
    }

    /// <summary>
    /// Spawns the one-off boss reveal AnimatedSprite2D behind the warrior death sprite.
    /// Texture: res://Assets/Enemies/8_Sword_Warrior/8_Sword_Warrior_Red/8_sword_warrior__red_boss_reveal.png
    /// Expected layout: 12 frames × 160×160 px, 12fps, no loop. If the texture is missing,
    /// falls through to SwapToPhase2 so the game never softlocks during early development.
    /// </summary>
    private void SpawnBossReveal()
    {
        if (_phaseTransitionConsumed) return;  // guard against late fire after a direct swap

        const string revealPath = "res://Assets/Enemies/8_Sword_Warrior/8_Sword_Warrior_Red/8_sword_warrior__red_boss_reveal.png";
        const string idlePath   = "res://Assets/Enemies/8_Sword_Warrior/8_Sword_Warrior_Red/8_sword_warrior_red-Sheet.png";
        var revealTexture = GD.Load<Texture2D>(revealPath);
        if (revealTexture == null)
        {
            GD.PrintErr($"[BattleTest] Boss reveal sprite missing ({revealPath}) — skipping reveal and swapping directly.");
            SwapToPhase2();
            return;
        }
        var idleTexture = GD.Load<Texture2D>(idlePath);
        if (idleTexture == null)
        {
            GD.PrintErr($"[BattleTest] 8 Sword Warrior idle sheet missing ({idlePath}) — reveal will play without the idle tail.");
        }

        const int frameSize        = 160;
        const int revealFrameCount = 12;
        const int idleFrameCount   = 14;   // row 0, 14 frames
        const int idleRow          = 0;
        const int idleCycles       = 3;    // append 3 full idle cycles so the reveal
                                           // keeps idling even if the warrior death
                                           // animation runs long. ApplyPhase2Sprite
                                           // frees the reveal when death completes,
                                           // so trailing cycles are cut off naturally.
        const float fps = 12f;

        var frames = new SpriteFrames();
        if (frames.HasAnimation("default")) frames.RemoveAnimation("default");
        frames.AddAnimation("default");
        frames.SetAnimationSpeed("default", fps);
        frames.SetAnimationLoop("default", false);

        // Reveal frames (horizontal strip).
        for (int i = 0; i < revealFrameCount; i++)
        {
            var atlas = new AtlasTexture();
            atlas.Atlas  = revealTexture;
            atlas.Region = new Rect2(i * frameSize, 0, frameSize, frameSize);
            frames.AddFrame("default", atlas);
        }

        // Append idle frames from the 8 Sword Warrior main sheet, repeated for
        // `idleCycles` full passes. The combined animation plays reveal → idle ×N in
        // one linear pass; with loop=false it holds on the final idle frame if it
        // outlasts the warrior death. ApplyPhase2Sprite frees the reveal when the
        // warrior death AnimationFinished fires, so any unplayed cycles are cut off.
        if (idleTexture != null)
        {
            for (int cycle = 0; cycle < idleCycles; cycle++)
            {
                for (int i = 0; i < idleFrameCount; i++)
                {
                    var atlas = new AtlasTexture();
                    atlas.Atlas  = idleTexture;
                    atlas.Region = new Rect2(i * frameSize, idleRow * frameSize, frameSize, frameSize);
                    frames.AddFrame("default", atlas);
                }
            }
        }

        _revealSprite = new AnimatedSprite2D();
        _revealSprite.SpriteFrames = frames;
        _revealSprite.Centered     = true;
        _revealSprite.Scale        = new Vector2(3f, 3f);
        // The source reveal sheet faces right; the 8 Sword Warrior faces left in
        // gameplay. Mirror horizontally so orientation matches.
        _revealSprite.FlipH        = true;
        // ZIndex strategy for the reveal sequence — everything stays ≥ 0 so the
        // scene's default-ZIndex Background ColorRect (at 0, first in tree order)
        // stays at the bottom:
        //   Background: 0 (unchanged in scene)
        //   Reveal:     1
        //   Warrior:    2 (bumped; teardown clobber is guarded below)
        //   Effects:    3 (set in BattleSystem.SpawnEffectSprite)
        // PlayTeardown from the killing attack clobbers the warrior's ZIndex back to 0
        // at the end of its tween, which would knock it below the reveal. That clobber
        // is now guarded with an _enemyDead check so the dying warrior's bumped ZIndex
        // survives until SwapToPhase2 restores the snapshot value.
        _enemyZIndexBeforeReveal = _enemyAnimSprite.ZIndex;
        _enemyAnimSprite.ZIndex  = 2;
        _revealSprite.ZIndex     = 1;
        GD.Print($"[BossReveal] ZIndex set — reveal: {_revealSprite.ZIndex}, warrior: {_enemyAnimSprite.ZIndex} " +
                 $"(snapshot was {_enemyZIndexBeforeReveal}; background at 0)");
        // Attach under the same parent as the warrior sprite and use its LOCAL position
        // so the reveal shares whatever coordinate space the warrior lives in — including
        // any camera/viewport transform chain that would misplace a world-coord child of
        // the BattleTest root.
        var enemyParent = _enemyAnimSprite.GetParent();
        enemyParent.AddChild(_revealSprite);
        // Shift the reveal up 10px so its final frame lines up more closely with the
        // Phase 2 idle pose, reducing the vertical jump at the swap moment.
        _revealSprite.Position = _enemyAnimSprite.Position + new Vector2(0f, -10f);
        _revealSprite.Play("default");
        GD.Print("[BattleTest] Boss reveal sprite spawned.");
    }

    /// <summary>
    /// Visual-only half of the Phase 2 swap: frees the reveal sprite, reassigns EnemyData,
    /// rebuilds SpriteFrames, repositions, restores the warrior ZIndex, and starts idle.
    /// Idempotent — guarded by _phase2SpriteApplied so SwapToPhase2 can safely call it
    /// again on the SkipPhaseTransition path.
    /// </summary>
    private void ApplyPhase2Sprite()
    {
        if (_phase2SpriteApplied) return;
        _phase2SpriteApplied     = true;
        // Point-of-no-return: the warrior is gone on screen from this moment. Any
        // subsequent enemy death (Phase 2) must go through the normal Victory path,
        // not retrigger the transition. Setting this BEFORE any other logic means
        // even a re-entrant death-trigger in the same tick is blocked by
        // IsPhaseTransitionPending on its next check.
        _phaseTransitionConsumed = true;

        if (_revealSprite != null)
        {
            _revealSprite.QueueFree();
            _revealSprite = null;
        }

        // Drop every named AnimationFinished handler from the Phase 1 warrior before
        // the Phase 2 sprite starts playing. Without this, a stale OnEnemyDeathFinished
        // connection from the warrior's death fires on the first Phase 2 animation
        // completion (idle → hurt → cast_intro etc.) and incorrectly triggers Victory.
        // We also clear the other turn-scoped handlers defensively.
        SafeDisconnectEnemyAnim(OnEnemyDeathFinished);
        SafeDisconnectEnemyAnim(OnCastIntroFinished);
        SafeDisconnectEnemyAnim(OnCastEndFinished);
        SafeDisconnectEnemyAnim(OnEnemyAttackAnimFinished);
        SafeDisconnectEnemyAnim(OnEnemyHurtFlashFinished);

        EnemyData  = Phase2EnemyData;
        _enemyDead = false;  // clear BEFORE PlayEnemy so the _enemyDead guard doesn't block idle

        // Force BuildEnemySpriteFrames to rebuild — the "already built" early-return
        // checks for an existing idle animation, so null out SpriteFrames first.
        _enemyAnimSprite.SpriteFrames = null;
        BuildEnemySpriteFrames();

        // Restore the warrior sprite's ZIndex to whatever it was before the reveal bumped
        // it. The warrior is gone now; the Phase 2 sprite takes over the slot at its
        // original layer.
        _enemyAnimSprite.ZIndex = _enemyZIndexBeforeReveal;

        // Re-apply floor-anchored positioning with the new enemy's dimensions and offset.
        // Mirrors the formula in _Ready so Phase 2 lands correctly on the ground line.
        float enemyFh      = EnemyData.FrameHeight;
        float enemyOffsetY = EnemyData.SpriteOffsetY;
        _enemyAnimSprite.Scale    = new Vector2(3f, 3f);
        _enemyAnimSprite.Position = new Vector2(_enemyAnimSprite.Position.X,
                                                FloorY - enemyFh * 3f * 0.6f + enemyOffsetY);
        _enemyAnimSpriteOrigin    = _enemyAnimSprite.Position;
        _enemyAnimSprite.Play("idle");
    }

    /// <summary>
    /// State-only half of the Phase 2 swap: clears per-fight flags, resets HP, updates
    /// the name label + HP bar, and returns control to the player. The sprite work
    /// already happened earlier in ApplyPhase2Sprite (unless SkipPhaseTransition is set,
    /// in which case it runs here as a fallback).
    /// </summary>
    private void SwapToPhase2()
    {
        // Guard against double-invocation of the state-finalisation step. Note the
        // point-of-no-return flag (_phaseTransitionConsumed) was already set in
        // ApplyPhase2Sprite — we can't reuse it here because on the normal path it's
        // true by now but the finalisation still needs to run exactly once.
        if (_phase2Finalised) return;
        _phase2Finalised = true;
        GD.Print($"[BattleTest] Finalising Phase 2 swap: {Phase2EnemyData.EnemyName}.");

        // SkipPhaseTransition path: OnBossRevealFinished never ran, so do the sprite
        // work now. No-op on the normal path (ApplyPhase2Sprite already ran).
        // ApplyPhase2Sprite also sets _phaseTransitionConsumed as a belt-and-suspenders
        // guarantee for the SkipPhaseTransition path.
        ApplyPhase2Sprite();

        // Per-fight flags that must not carry over from Phase 1 into Phase 2.
        // Critically: _hasAbsorbedLearnableMove must reset so the Phase 2 learnable
        // can be absorbed. Other flags are one-shot or turn-scoped but are cleared
        // here for defense-in-depth against stale state from the final Phase 1 turn.
        _hasAbsorbedLearnableMove = false;
        _beckoning                = false;
        _playerDefending          = false;
        _parryClean               = false;
        _pendingGameOver          = false;
        _hopInOver                = false;
        _hopInSequenceCompleted   = false;
        _hopInAnimFinished        = false;

        // Reset HP and UI. EnemyData was reassigned in ApplyPhase2Sprite above, so
        // reading EnemyData.MaxHp here pulls the Phase 2 value.
        _enemyMaxHP = EnemyData.MaxHp;
        _enemyHP    = EnemyData.MaxHp;
        if (_enemyNameLabel != null) _enemyNameLabel.Text = EnemyData.EnemyName;
        UpdateHPBars();

        // Reset attack-pool rotation so the new pool starts fresh.
        _lastAttackIndex = -1;

        // Brief pause before the player regains control in Phase 2.
        GetTree().CreateTimer(0.5f).Timeout += ShowMenu;
        // TODO: Phase 2 music cue goes here.
    }

    // -------------------------------------------------------------------------
    // Combo and retreat handlers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fires when combo_slash1 (frames 1–3) finishes after pass 0.
    /// Holds on "combo" frame 5 — the second wind-up — so the sprite is posed ready
    /// for combo_slash2 while the outward bounce travels back out.
    /// Stop() is called before setting Frame to counteract Godot 4's reset-to-0 on stop.
    /// </summary>
    private void OnComboPass0SlashFinished()
    {
        SafeDisconnectPlayerAnim(OnComboPass0SlashFinished);
        if (_playerDead) return;
        if (_comboMissed)
        {
            // Pass 0 was a miss — skip the wind-up hold and retreat immediately.
            _comboMissed = false;
            BeginComboMissRetreat();
            return;
        }
        _playerAnimSprite.Animation = "combo";
        StopPlayer();
        SetPlayerFrame(5);  // OWNER: combo pass 0 resolved — wind-up before slash2 (sheet frame 5)
    }

    /// <summary>
    /// Fires when combo_slash2 (frames 6–9) finishes after pass 1.
    /// Holds on "combo" frame 0 — the first wind-up again — ready for combo_slash1 on the final pass.
    /// Stop() is called before setting Frame to counteract Godot 4's reset-to-0 on stop.
    /// </summary>
    private void OnComboPass1SlashFinished()
    {
        SafeDisconnectPlayerAnim(OnComboPass1SlashFinished);
        if (_playerDead) return;
        if (_comboMissed)
        {
            // Pass 1 was a miss — skip the wind-up hold and retreat immediately.
            _comboMissed = false;
            BeginComboMissRetreat();
            return;
        }
        _playerAnimSprite.Animation = "combo";
        StopPlayer();
        SetPlayerFrame(0);  // OWNER: combo pass 1 resolved — wind-up before slash1 again (sheet frame 0)
    }

    /// <summary>
    /// Triggered when the combo is cancelled by a miss on pass 0 or pass 1.
    /// Mirrors the retreat logic in OnFinalSlashFinished: short hold then hop-back + enemy turn.
    /// </summary>
    private void BeginComboMissRetreat()
    {
        // The animation already finished (that's what triggered the callback), so the sprite
        // is naturally holding its last frame. Calling Stop() here would reset Frame to 0 in
        // Godot 4 — don't touch the frame; just start the retreat from the current pose.
        if (_pendingGameOver)
        {
            // Damage from the miss somehow killed the enemy (edge case).
            _enemyDead = true;
            PlaySound("enemy_defeat.mp3");
            SafeDisconnectEnemyAnim(OnEnemyDeathFinished);
            _enemyAnimSprite.Play("death");  // OWNER: enemy death from combo miss damage
            _enemyAnimSprite.AnimationFinished += OnEnemyDeathFinished;
            ScheduleBossRevealIfPhase1();
            // Retreat the player to origin and return to idle — mirrors the non-game-over
            // path below so the player doesn't freeze on the last combo miss frame.
            GetTree().CreateTimer(0.3f).Timeout += () =>
            {
                if (_playerDead) return;
                _playerAnimSprite.SpriteFrames.SetAnimationLoop("run", false);
                _playerAnimSprite.SpeedScale = 2f;
                SafeDisconnectPlayerAnim(OnRetreatFinished);
                PlayPlayerBackwards("run");  // OWNER: killing-blow retreat (combo miss)
                _playerAnimSprite.AnimationFinished += OnRetreatFinished;
                PlayTeardown(null);
            };
            return;
        }
        GetTree().CreateTimer(0.3f).Timeout += () =>
        {
            _playerAnimSprite.SpriteFrames.SetAnimationLoop("run", false);
            _playerAnimSprite.SpeedScale = 2f;
            SafeDisconnectPlayerAnim(OnRetreatFinished);
            PlayPlayerBackwards("run");  // OWNER: BeginComboMissRetreat — retreat hop-back
            _playerAnimSprite.AnimationFinished += OnRetreatFinished;
            PlayTeardown(() => GetTree().CreateTimer(0.5f).Timeout += BeginEnemyAttack);
        };
    }

    /// <summary>
    /// Fires when the final slash animation completes (combo_slash1 for single, combo_slash2 for combo).
    /// Holds the last frame for 0.3s so the strike reads, then starts the retreat:
    /// disables looping on "run" so PlayBackwards fires AnimationFinished at frame 0,
    /// then launches the position teardown and backwards run animation together.
    /// PlayTeardown is called here rather than in OnPlayerPromptCompleted so the slash always
    /// completes before the combatant starts moving back.
    /// </summary>
    private void OnFinalSlashFinished()
    {
        SafeDisconnectPlayerAnim(OnFinalSlashFinished);
        StopPlayer();  // OWNER: OnFinalSlashFinished — hold last slash frame (sheet frame 3 or 9)
        // Godot 4 resets Frame to 0 when Stop() is called on a finished non-looping animation.
        // Re-apply the last frame index explicitly to counteract this and hold the final pose.
        SetPlayerFrame(_playerAnimSprite.SpriteFrames.GetFrameCount("combo_slash1") - 1);

        if (_pendingGameOver)
        {
            // Enemy HP reached zero from the player's attack.
            // (Player cannot die during their own attack; _pendingGameOver here always means enemy defeated.)
            // Interrupt the enemy's current animation and play death; reset camera without next turn.
            _enemyDead = true;
            PlaySound("enemy_defeat.mp3");
            SafeDisconnectEnemyAnim(OnEnemyDeathFinished);
            _enemyAnimSprite.Play("death");         // OWNER: enemy death from player attack
            _enemyAnimSprite.AnimationFinished += OnEnemyDeathFinished;
            ScheduleBossRevealIfPhase1();
            // Retreat the player to origin and return to idle — same hop-back treatment
            // as the non-game-over path. Without this the player freezes on the last
            // slash frame (PlayTeardown would tween the ColorRect back but no run
            // animation is played and OnRetreatFinished never fires PlayPlayer("idle")).
            // Required for the phase transition so Phase 2 starts with the player idling.
            GetTree().CreateTimer(0.3f).Timeout += () =>
            {
                if (_playerDead) return;
                _playerAnimSprite.SpriteFrames.SetAnimationLoop("run", false);
                _playerAnimSprite.SpeedScale = 2f;
                SafeDisconnectPlayerAnim(OnRetreatFinished);
                PlayPlayerBackwards("run");  // OWNER: killing-blow retreat
                _playerAnimSprite.AnimationFinished += OnRetreatFinished;
                PlayTeardown(null);
            };
            return;
        }

        // OWNER: OnFinalSlashFinished (player turn, retreat begins).
        // Hold the last slash frame for 0.3s so it reads before the character moves,
        // then start the retreat. PlayTeardown and PlayBackwards begin together after
        // the pause so the position tween and animation are in sync.
        GetTree().CreateTimer(0.3f).Timeout += () =>
        {
            // Disable looping on "run" for this one-shot backwards pass so that
            // AnimationFinished fires when frame 0 is reached and OnRetreatFinished triggers.
            // Looping is intentional for the forward hop-in; we restore it in OnRetreatFinished.
            _playerAnimSprite.SpriteFrames.SetAnimationLoop("run", false);
            _playerAnimSprite.SpeedScale = 2f;
            SafeDisconnectPlayerAnim(OnRetreatFinished);
            PlayPlayerBackwards("run");  // OWNER: player turn, retreat hop-back
            _playerAnimSprite.AnimationFinished += OnRetreatFinished;

            PlayTeardown(() => GetTree().CreateTimer(0.5f).Timeout += BeginEnemyAttack);
        };
    }

    /// <summary>
    /// Called when the backwards run animation reaches frame 0 after the player hops back.
    /// Always resets SpeedScale so subsequent animations aren't affected.
    /// Only restores idle if the sprite is still on "run" — if an enemy-side handler (parry, hit)
    /// took ownership while the retreat was in flight, that animation takes priority.
    /// </summary>
    private void OnRetreatFinished()
    {
        SafeDisconnectPlayerAnim(OnRetreatFinished);
        _playerAnimSprite.SpeedScale = 1f;  // always reset — SpeedScale affects all animations
        // Restore looping on "run" — it was disabled before PlayBackwards so AnimationFinished
        // would fire once at frame 0. The forward hop-in on the next player turn needs it looping.
        _playerAnimSprite.SpriteFrames.SetAnimationLoop("run", true);
        // Guard: only return to idle if the retreat run still owns the sprite.
        // OnBattleSystemStepPassEvaluated may have pre-empted this handler and started
        // parry or hit; in that case let those complete without overriding them.
        if (_playerAnimSprite.Animation == "run")
            PlayPlayer("idle");  // OWNER: OnRetreatFinished — retreat complete
    }

    // =========================================================================
    // Perfect parry counter animation
    // =========================================================================

    /// <summary>
    /// Plays a staged counter attack animation after a perfect parry:
    ///   1. attack1 frame 0 held 0.5s (wind-up anticipation)
    ///   2. attack1 frame 1 held 0.3s (impact pose) — spawns anime slash effect + enemy shake
    ///   3. attack1 frames 2-3 play through as normal follow-through
    ///   4. After the slash animation finishes (~1.25s): applies 20 counter damage, updates HP
    ///   5. Calls onComplete to resume the original post-sequence flow
    /// </summary>
    private void PlayParryCounter(Action onComplete)
    {
        const int CounterDamage = 20;
        PlaySound("perfect_parry_shimmer_2.wav");

        // Disconnect cast_end callback so it doesn't fire during the counter.
        // OnEnemyAttackAnimFinished is NOT disconnected — it must still set _hopInAnimFinished
        // for the hop-in rendezvous. Its idle call is guarded below.
        SafeDisconnectEnemyAnim(OnCastEndFinished);
        if (HasCastEnd())
        {
            PlayEnemy("cast_end");
            _enemyAnimSprite.AnimationFinished += OnCastEndFinished;
        }
        else if (!_battleSystem.CurrentAttackIsHopIn)
        {
            // Non-hop-in cast attacks: transition from cast_loop to idle.
            // Hop-in melee attacks: let melee_attack play to completion —
            // OnEnemyAttackAnimFinished handles the transition.
            PlayEnemy("idle");
        }

        // Player stays in parry animation until OnParryFinished fires naturally,
        // which transitions to idle. No need to force idle here.

        // Wind-up: after parry completes, hold attack1 frame 0 for anticipation.
        GetTree().CreateTimer(0.5f).Timeout += () =>
        {
            if (_playerDead) { onComplete?.Invoke(); return; }

            // Hold attack1 frame 0 (wind-up pose) for 0.3s.
            _playerAnimSprite.Animation = "attack1";
            StopPlayer();
            SetPlayerFrame(0);  // OWNER: PlayParryCounter — wind-up anticipation hold

            GetTree().CreateTimer(0.3f).Timeout += () =>
            {
                if (_playerDead) { onComplete?.Invoke(); return; }

                // Impact: snap to frame 1, spawn slash effect + shake.
                PlaySound("counter_swing.wav");
                PlaySound("player_attack_swing.wav");
                SetPlayerFrame(1);  // OWNER: PlayParryCounter — impact pose

                // Play enemy hurt reaction during parry counter impact.
                // Enemies with a separate hurt sheet use hurt_full (14 frames) looped
                // from frame 3 for a sustained hurt pose. Others use "hurt" (short, no loop).
                Action onHurtFullFinished = null;
                if (HasSeparateHurtSheet())
                {
                    onHurtFullFinished = () =>
                    {
                        SafeDisconnectEnemyAnim(onHurtFullFinished);
                        if (_enemyDead) return;
                        PlayEnemy("hurt_full");
                        _enemyAnimSprite.Frame = 3;
                        _enemyAnimSprite.AnimationFinished += onHurtFullFinished;
                    };
                    SafeDisconnectEnemyAnim(onHurtFullFinished);
                    PlayEnemy("hurt_full");
                    _enemyAnimSprite.AnimationFinished += onHurtFullFinished;
                }
                else
                {
                    // Simple hurt animation — play once then freeze on last frame until
                    // the counter slash effect completes and the callback plays idle.
                    onHurtFullFinished = () =>
                    {
                        SafeDisconnectEnemyAnim(onHurtFullFinished);
                        if (_enemyDead) return;
                        // Explicitly stop and hold on the last hurt frame so the enemy
                        // stays in the hurt pose for the duration of the slash effect.
                        _enemyAnimSprite.Stop();
                    };
                    SafeDisconnectEnemyAnim(onHurtFullFinished);
                    PlayEnemy("hurt");
                    _enemyAnimSprite.AnimationFinished += onHurtFullFinished;
                }

                // Spawn anime slash effect centered on the enemy.
                // Damage is deferred until the slash animation completes.
                PlaySound("counter_slash_multi.wav");
                SpawnCounterSlashEffect(() =>
                {
                    // Break the hurt loop and apply counter damage.
                    SafeDisconnectEnemyAnim(onHurtFullFinished);
                    SafeDisconnectEnemyAnim(OnCastEndFinished);
                    PlayEnemy("idle");
                    _enemyHP = Mathf.Max(0, _enemyHP - CounterDamage);
                    GD.Print($"[BattleTest] Perfect parry! Auto counter: {CounterDamage} damage. Enemy HP: {_enemyHP}/{_enemyMaxHP}");
                    PlaySound("enemy_hit.wav");
                    // Spawn damage number at the enemy's current world position, offset
                    // upward above the head. Parented to BattleTest root (not the sprite)
                    // so scale inheritance doesn't affect label size or float speed.
                    float fh = EnemyData?.FrameHeight ?? 160;
                    float sy = _enemyAnimSprite.Scale.Y;
                    Vector2 counterDmgPos = new Vector2(_enemyAnimSprite.GlobalPosition.X,
                                                        _enemyAnimSprite.GlobalPosition.Y - fh * sy * 0.3f + 50f);
                    SpawnDamageNumber(counterDmgPos, CounterDamage, DmgColorPerfect);
                    ShakeCamera(intensity: 10f, duration: 0.3f);
                    UpdateHPBars();
                    PlayPlayer("idle");  // OWNER: PlayParryCounter — slash done, release held pose
                    // Brief pause so the damage number reads before the retreat/teardown begins.
                    GetTree().CreateTimer(0.5f).Timeout += () => onComplete?.Invoke();
                });

                // Shake the enemy sprite for the full duration of the slash (~1.25s).
                ShakeEnemySprite(passes: 12, duration: 1.25f, intensity: 6f);

                // Follow-through: after a short beat, play frames 2-3 then return to idle.
                GetTree().CreateTimer(0.2f).Timeout += () =>
                {
                    if (_playerDead) return;
                    // Play attack1 from frame 2 onward (frames 2-3 are the follow-through).
                    PlayPlayer("attack1");
                    _playerAnimSprite.Frame = 2;  // skip to follow-through frames

                    Action onFollowThroughFinished = null;
                    onFollowThroughFinished = () =>
                    {
                        SafeDisconnectPlayerAnim(onFollowThroughFinished);
                        // Hold on frame 3 (last attack1 frame) until slash effect completes.
                        StopPlayer();
                        SetPlayerFrame(3);  // OWNER: PlayParryCounter — hold final pose until slash done
                    };
                    SafeDisconnectPlayerAnim(onFollowThroughFinished);
                    _playerAnimSprite.AnimationFinished += onFollowThroughFinished;
                };
            };
        };
    }

    /// <summary>
    /// Spawns the anime_slash_grey_Sheet.png effect centered on the enemy AnimatedSprite2D.
    /// The effect plays its full 15 frames at 12fps (~1.25s) then frees itself.
    /// <paramref name="onComplete"/> fires after the animation finishes and the sprite is freed.
    /// </summary>
    private void SpawnCounterSlashEffect(Action onComplete = null)
    {
        const string SheetPath = "res://Assets/Effects/Sword_Effects/Anime_Slash_Grey_Sheet.png";
        const int    Fw        = 128;
        const int    Fh        = 128;
        const int    FrameCount = 15;
        const float  Fps       = 12f;

        var texture = GD.Load<Texture2D>(SheetPath);
        if (texture == null)
        {
            GD.PrintErr($"[BattleTest] Failed to load counter slash effect: {SheetPath}");
            return;
        }

        var frames = new SpriteFrames();
        if (frames.HasAnimation("default")) frames.RemoveAnimation("default");
        frames.AddAnimation("slash");
        frames.SetAnimationSpeed("slash", Fps);
        frames.SetAnimationLoop("slash", false);

        for (int i = 0; i < FrameCount; i++)
        {
            var atlas    = new AtlasTexture();
            atlas.Atlas  = texture;
            atlas.Region = new Rect2(i * Fw, 0, Fw, Fh);
            frames.AddFrame("slash", atlas);
        }

        var sprite = new AnimatedSprite2D();
        sprite.SpriteFrames = frames;
        sprite.Centered     = true;
        sprite.Scale        = new Vector2(3f, 3f);
        sprite.Position     = _enemyAnimSprite.Position;
        AddChild(sprite);
        sprite.Play("slash");

        // Self-destruct when done, then fire the completion callback.
        Action onFinished = null;
        onFinished = () =>
        {
            sprite.AnimationFinished -= onFinished;
            sprite.QueueFree();
            onComplete?.Invoke();
        };
        sprite.AnimationFinished += onFinished;
    }

    /// <summary>
    /// Rapidly oscillates the enemy sprite horizontally to convey impact during the counter slash.
    /// </summary>
    private void ShakeEnemySprite(int passes, float duration, float intensity)
    {
        if (_enemyDead) return;
        Vector2 origin = _enemyAnimSprite.Position;
        float passTime = duration / passes;

        var tween = CreateTween();
        for (int i = 0; i < passes; i++)
        {
            float dir = (i % 2 == 0) ? intensity : -intensity;
            tween.TweenProperty(_enemyAnimSprite, "position:x", origin.X + dir, passTime * 0.5f);
            tween.TweenProperty(_enemyAnimSprite, "position:x", origin.X,       passTime * 0.5f);
        }
    }

    // =========================================================================
    // Enemy hurt reaction
    // =========================================================================

    /// <summary>
    /// Plays the enemy's short hurt reaction animation then returns to idle.
    /// Enemies with a separate hurt sheet use "hurt_flash"; others use "hurt".
    /// </summary>
    private void PlayEnemyHurtFlash()
    {
        if (_enemyDead) return;
        SafeDisconnectEnemyAnim(OnEnemyHurtFlashFinished);
        string hurtAnim = HasSeparateHurtSheet() ? "hurt_flash" : "hurt";
        PlayEnemy(hurtAnim);
        _enemyAnimSprite.AnimationFinished += OnEnemyHurtFlashFinished;
    }

    /// <summary>True when the enemy uses a separate spritesheet for hurt animations (hurt_flash + hurt_full).</summary>
    private bool HasSeparateHurtSheet() =>
        EnemyData?.AnimationConfig != null && !string.IsNullOrEmpty(EnemyData.AnimationConfig.HurtSheetPath);

    /// <summary>True when the enemy has a cast_end animation registered.</summary>
    private bool HasCastEnd() =>
        EnemyData?.AnimationConfig?.HasCastEnd ?? false;

    private void OnEnemyHurtFlashFinished()
    {
        SafeDisconnectEnemyAnim(OnEnemyHurtFlashFinished);
        PlayEnemy("idle");
    }

    // =========================================================================
    // Sprite play guards
    // =========================================================================
    // All Play / PlayBackwards / Stop / Frame= calls on the two animated sprites
    // route through these helpers. Once a dead flag is set the sprite holds its
    // final death frame — no subsequent animation call can override it.

    /// <summary>Calls _playerAnimSprite.Play(anim) only if the player is not dead.</summary>
    private void PlayPlayer(string anim)          { if (!_playerDead) _playerAnimSprite.Play(anim); }

    /// <summary>Calls _playerAnimSprite.PlayBackwards(anim) only if the player is not dead.</summary>
    private void PlayPlayerBackwards(string anim) { if (!_playerDead) _playerAnimSprite.PlayBackwards(anim); }

    /// <summary>Calls _playerAnimSprite.Stop() only if the player is not dead.</summary>
    private void StopPlayer()                     { if (!_playerDead) _playerAnimSprite.Stop(); }

    /// <summary>Assigns _playerAnimSprite.Frame only if the player is not dead.</summary>
    private void SetPlayerFrame(int frame)        { if (!_playerDead) _playerAnimSprite.Frame = frame; }

    /// <summary>Calls _enemyAnimSprite.Play(anim) only if the enemy is not dead.</summary>
    private void PlayEnemy(string anim)           { if (!_enemyDead)  _enemyAnimSprite.Play(anim); }

    /// <summary>
    /// Disconnects <paramref name="handler"/> from _playerAnimSprite.AnimationFinished only if
    /// it is currently connected. Prevents the "Attempt to disconnect a nonexistent connection"
    /// error that fires when a pre-emptive or redundant -= is issued on an unconnected handler.
    /// </summary>
    private void SafeDisconnectPlayerAnim(Action handler)
    {
        var callable = Callable.From(handler);
        if (_playerAnimSprite.IsConnected(AnimatedSprite2D.SignalName.AnimationFinished, callable))
            _playerAnimSprite.Disconnect(AnimatedSprite2D.SignalName.AnimationFinished, callable);
    }

    /// <summary>
    /// Disconnects <paramref name="handler"/> from _enemyAnimSprite.AnimationFinished only if
    /// it is currently connected. Mirrors SafeDisconnectPlayerAnim for the enemy sprite.
    /// </summary>
    private void SafeDisconnectEnemyAnim(Action handler)
    {
        var callable = Callable.From(handler);
        if (_enemyAnimSprite.IsConnected(AnimatedSprite2D.SignalName.AnimationFinished, callable))
            _enemyAnimSprite.Disconnect(AnimatedSprite2D.SignalName.AnimationFinished, callable);
    }

    // =========================================================================
    // End-of-battle overlay
    // =========================================================================

    /// <summary>
    /// Creates a large white label — "Game Over" or "Victory!" — centered on screen
    /// on a dedicated CanvasLayer so it renders above all game elements.
    /// The label starts fully transparent and fades in to opaque over 0.5 seconds.
    /// Called once; no battle restart follows.
    /// </summary>
    private void ShowEndLabel(string text)
    {
        // Idempotent — player-death sites call ShowEndLabel at death-start (so the Game Over
        // overlay and music fade appear immediately, before the enemy's sequence finishes),
        // and OnPlayerDeathFinished also calls it at death-anim completion. Second call no-ops.
        if (_endLabelShown) return;
        _endLabelShown = true;

        // Fade the battle music out before the sting plays. Applies to both Victory and
        // Game Over — same 1.5s fade, then Stop. Safe to call if music isn't currently
        // playing (no-op when _musicPlayer.Playing is false).
        FadeOutMusic(1.5f);

        if (text.Contains("Victory"))
            PlaySound("victory.wav");
        else if (text.Contains("Game Over"))
            PlaySound("game_over.mp3");

        var layer = new CanvasLayer();
        AddChild(layer);

        var label = new Label();
        label.Text                = text;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment   = VerticalAlignment.Center;
        label.AddThemeFontSizeOverride("font_size", 64);
        label.Modulate = new Color(1f, 1f, 1f, 0f);  // start transparent; Tween fades in below

        // Stretch the label to fill the entire CanvasLayer viewport so the text centers
        // correctly regardless of resolution. Anchors: (0,0)→(1,1), offsets cleared.
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        label.OffsetLeft   = 0f;
        label.OffsetTop    = 0f;
        label.OffsetRight  = 0f;
        label.OffsetBottom = 0f;
        layer.AddChild(label);

        var tween = CreateTween();
        tween.TweenProperty(label, "modulate:a", 1.0f, 0.5f);
    }
}
