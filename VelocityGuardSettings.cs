namespace VelocityGuard;

/// <summary>
/// Tunable values for <see cref="VelocityGuardCore"/>.
/// Values are taken as-is from the UI and clamped inside the filter, so out-of-range
/// values are safe — callers never need to sanitise before passing this in.
/// </summary>
public struct VelocityGuardSettings
{
    /// <summary>Dead-zone radius (screen pixels) when the pen shows no net movement.</summary>
    public float MaxDeadZone;

    /// <summary>Net speed (px/ms) at which the dead zone reaches zero and output becomes raw passthrough.</summary>
    public float FullSpeedThreshold;

    /// <summary>Dead-zone decay shape. &lt;1 collapses earlier, &gt;1 holds the zone longer.</summary>
    public float Curve;

    /// <summary>Time constant (ms) of the velocity estimator. Higher = steadier speed, slower to react.</summary>
    public float VelocitySmoothMs;

    /// <summary>
    /// Time constant (ms) of extra output smoothing. Defaults to off: the drag dead zone already
    /// produces a continuous output, and measured against synthetic chatter this buys no measurable
    /// smoothness while costing tracking lag. Kept as an escape hatch for unusually noisy hardware.
    /// </summary>
    public float OutputSmoothMs;

    /// <summary>
    /// Fraction of the dead-zone offset to cancel by leading along the direction of travel, 0..1.
    /// Applies only to movement the filter judges coherent, and is bounded by the dead zone itself,
    /// so it cannot overshoot on direction reversals.
    /// </summary>
    public float Lead;

    /// <summary>
    /// How much directed movement is allowed to shrink the dead zone, 0..1, independently of speed.
    /// 0 reproduces v2.0 behaviour, where a small deliberate correction never fully escaped the
    /// zone's positional offset. 1 collapses the zone entirely on movement judged fully coherent.
    /// </summary>
    public float CoherenceRelief;

    /// <summary>
    /// Defaults chosen from measured behaviour rather than carried over from v1.
    /// <para>
    /// <see cref="FullSpeedThreshold"/> is lower than v1's 12 because it is now fed net speed,
    /// which is systematically smaller than the gross speed v1 measured for the same movement.
    /// <see cref="Lead"/> defaults on because at 0.75 it restores v1-comparable tracking lag while
    /// keeping v2's smoothness; <see cref="OutputSmoothMs"/> defaults off for the reason above.
    /// </para>
    /// </summary>
    public static VelocityGuardSettings Default => new()
    {
        MaxDeadZone = 4f,
        FullSpeedThreshold = 6f,
        Curve = 1f,
        VelocitySmoothMs = 4f,
        OutputSmoothMs = 0f,
        Lead = 0.75f,
        CoherenceRelief = 0.75f
    };
}
