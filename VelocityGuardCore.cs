using System;
using System.Numerics;

namespace VelocityGuard;

/// <summary>
/// VelocityGuard v2 filter core — pure math, no OpenTabletDriver types.
/// Kept free of driver dependencies so it can be unit tested and mirrored by the web
/// simulator in docs/index.html without a driver in the loop.
/// </summary>
/// <remarks>
/// Differences from v1, each closing a measured defect:
/// <list type="number">
/// <item>Speed is the magnitude of the smoothed velocity <i>vector</i> (net speed) rather than the
/// smoothed magnitude of each step (gross speed). Chatter oscillates about a point, so its velocity
/// vectors cancel and net speed stays near zero — the dead zone no longer opens itself up under chatter.</item>
/// <item>Every smoothing coefficient is derived from elapsed time via <c>1 - exp(-dt/tau)</c>, so behaviour
/// no longer depends on tablet report rate or on USB interval jitter.</item>
/// <item>The dead zone drags the output behind the pen instead of gating it. The input→output map is
/// continuous across the zone boundary, which removes v1's stair-stepping and its jolt at the
/// filtered/unfiltered transition.</item>
/// <item>A stale timestamp (pen lift, daemon stall, wake) reseeds the filter instead of producing a
/// bogus velocity from a position the pen left long ago.</item>
/// </list>
/// </remarks>
public sealed class VelocityGuardCore
{
    /// <summary>Gap (ms) above which the pen is assumed to have left and returned, forcing a reseed.</summary>
    public const float ResetThresholdMs = 100f;

    /// <summary>
    /// Time constant (ms) of the estimators backing <see cref="Coherence"/>. Fixed rather than exposed:
    /// judging whether motion cancels itself out requires averaging over several oscillation periods,
    /// and any value in roughly 10–100 ms behaves the same, while the user-facing
    /// <see cref="VelocityGuardSettings.VelocitySmoothMs"/> is far too short to do the job.
    /// </summary>
    private const float CoherenceWindowMs = 25f;

    private const float MinDeltaMs = 1e-3f;
    private const float Epsilon = 1e-6f;
    private const float MinCurve = 0.01f;

    // ─── State ──────────────────────────────────────────────────────────────

    private Vector2 _lastInput;
    private Vector2 _lastOutput;
    private Vector2 _velocity;       // smoothed velocity vector, px/ms
    private float _speed;            // smoothed speed magnitude, px/ms
    private Vector2 _slowVelocity;   // same pair over CoherenceWindowMs, used only for coherence
    private float _slowSpeed;
    private bool _initialized;

    // ─── Diagnostics (for tests and the web simulator) ──────────────────────

    /// <summary>Magnitude of the smoothed velocity vector (px/ms). This is what drives the dead zone.</summary>
    public float NetSpeed { get; private set; }

    /// <summary>
    /// Directional consistency of recent movement, in [0,1]: the ratio of net to gross speed.
    /// Near 0 for chatter (motion that cancels itself out), near 1 for deliberate movement.
    /// </summary>
    public float Coherence { get; private set; }

    /// <summary>Dead-zone radius (px) produced by the most recent report.</summary>
    public float DeadZone { get; private set; }

    /// <summary>True once the filter holds valid state; false before the first report and after a reseed.</summary>
    public bool IsInitialized => _initialized;

    // ─── Entry point ────────────────────────────────────────────────────────

    /// <summary>
    /// Filters one position sample.
    /// </summary>
    /// <param name="input">Pen position in screen pixels.</param>
    /// <param name="dtMs">Milliseconds elapsed since the previous sample.</param>
    /// <param name="settings">Tunables; clamped internally.</param>
    /// <returns>The position to emit.</returns>
    public Vector2 Filter(Vector2 input, float dtMs, in VelocityGuardSettings settings)
    {
        // Never let a bad sample poison the state — hold instead.
        if (!IsFinite(input))
            return _initialized ? _lastOutput : input;

        // First report, a stale timestamp, or a nonsensical delta: reseed and pass through.
        if (!_initialized || !float.IsFinite(dtMs) || dtMs <= 0f || dtMs > ResetThresholdMs)
            return Seed(input);

        // Two samples with effectively the same timestamp carry no new velocity information.
        // Returning early also keeps _lastInput intact so the next real delta stays correct.
        if (dtMs < MinDeltaMs)
            return _lastOutput;

        // ── 1. Velocity, smoothed over time rather than over reports ────────
        Vector2 step = input - _lastInput;
        _lastInput = input;

        Vector2 instantVelocity = step / dtMs;
        float instantSpeed = instantVelocity.Length();

        float alpha = TimeAlpha(dtMs, settings.VelocitySmoothMs);
        _velocity = Vector2.Lerp(_velocity, instantVelocity, alpha);
        _speed = Lerp(_speed, instantSpeed, alpha);

        float slowAlpha = TimeAlpha(dtMs, CoherenceWindowMs);
        _slowVelocity = Vector2.Lerp(_slowVelocity, instantVelocity, slowAlpha);
        _slowSpeed = Lerp(_slowSpeed, instantSpeed, slowAlpha);

        // ── 2. Net speed and coherence ──────────────────────────────────────
        // |EMA(v)| <= EMA(|v|) by the triangle inequality, so the ratio is a genuine [0,1] measure
        // of how much of the recent motion actually went somewhere.
        NetSpeed = _velocity.Length();
        Coherence = _slowSpeed > Epsilon
            ? Math.Clamp(_slowVelocity.Length() / _slowSpeed, 0f, 1f)
            : 0f;

        // ── 3. Dead-zone radius ─────────────────────────────────────────────
        float threshold = MathF.Max(settings.FullSpeedThreshold, Epsilon);
        float maxDeadZone = MathF.Max(settings.MaxDeadZone, 0f);

        float t = Math.Clamp(NetSpeed / threshold, 0f, 1f);
        float shaped = MathF.Pow(t, MathF.Max(settings.Curve, MinCurve));
        DeadZone = maxDeadZone * (1f - shaped);

        // ── 4. Lead: cancel part of the offset the dead zone is about to impose ──
        // The magnitude is tied to DeadZone rather than to speed. A speed-proportional lead grows
        // exactly as the offset it is meant to cancel shrinks, so it overshoots on fast movement;
        // this form is bounded by DeadZone and vanishes with it at full speed. Cubing coherence
        // keeps it off during chatter, where the heading it would follow is meaningless.
        Vector2 effective = input;
        if (settings.Lead > 0f && Coherence > 0f && DeadZone > Epsilon)
        {
            float speedMagnitude = _velocity.Length();
            if (speedMagnitude > Epsilon)
            {
                float gate = Coherence * Coherence * Coherence;
                float magnitude = DeadZone * Math.Clamp(settings.Lead, 0f, 1f) * gate;
                effective = input + _velocity * (magnitude / speedMagnitude);
            }
        }

        // ── 5. Drag dead zone ───────────────────────────────────────────────
        // The output trails the pen by exactly DeadZone along the direction of travel. At the zone
        // boundary (distance == DeadZone) the target is identically the current output, so motion
        // begins from zero rather than in a step — this is what makes the map continuous.
        Vector2 toInput = effective - _lastOutput;
        float distance = toInput.Length();

        Vector2 target = distance > DeadZone && distance > Epsilon
            ? effective - toInput * (DeadZone / distance)
            : _lastOutput;

        // ── 6. Output smoothing, its time constant collapsing at full speed ─
        float k = TimeAlpha(dtMs, MathF.Max(settings.OutputSmoothMs, 0f) * (1f - shaped));

        // Assign the target outright at k >= 1 rather than trusting lerp to land exactly on it.
        // This is what guarantees bit-exact passthrough once the dead zone has fully collapsed.
        _lastOutput = k >= 1f ? target : Vector2.Lerp(_lastOutput, target, k);
        return _lastOutput;
    }

    /// <summary>Drops all state. The next report is treated as a fresh start and passes through.</summary>
    public void Reset()
    {
        _initialized = false;
        _velocity = Vector2.Zero;
        _speed = 0f;
        _slowVelocity = Vector2.Zero;
        _slowSpeed = 0f;
        NetSpeed = 0f;
        Coherence = 0f;
        DeadZone = 0f;
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private Vector2 Seed(Vector2 input)
    {
        _initialized = true;
        _lastInput = input;
        _lastOutput = input;
        _velocity = Vector2.Zero;
        _speed = 0f;
        _slowVelocity = Vector2.Zero;
        _slowSpeed = 0f;
        NetSpeed = 0f;
        Coherence = 0f;
        DeadZone = 0f;
        return input;
    }

    /// <summary>
    /// EMA weight for an elapsed time, giving a fixed time constant regardless of report rate.
    /// A time constant at or below the resolution floor means "no smoothing".
    /// </summary>
    private static float TimeAlpha(float dtMs, float tauMs)
        => tauMs <= MinDeltaMs ? 1f : 1f - MathF.Exp(-dtMs / tauMs);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static bool IsFinite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
}
