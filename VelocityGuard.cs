using System;
using System.Diagnostics;
using System.Numerics;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Tablet;

namespace VelocityGuard;

/// <summary>
/// VelocityGuard v2 — velocity-adaptive dead zone filter for OpenTabletDriver.
/// Suppresses pen chatter at rest and during slow aim, and hands through fast movement unaltered.
/// </summary>
/// <remarks>
/// This type is only the driver-facing shell: settings, pipeline placement and timing.
/// All filtering lives in <see cref="VelocityGuardCore"/>, which has no driver dependencies.
/// </remarks>
[PluginName("VelocityGuard")]
public class VelocityGuard : IPositionedPipelineElement<IDeviceReport>
{
    // ─── User-facing settings ────────────────────────────────────────────────
    //
    // Property names are the keys OTD stores settings under. Three of them deliberately differ
    // from v1: SpeedSmoothAlpha, MinSmoothFactor and PredictionStrength all changed both units
    // and meaning, so reusing their names would have silently reinterpreted saved values rather
    // than resetting them. v1 settings do not carry over.

    /// <summary>Dead-zone radius (screen pixels) when the pen shows no net movement.</summary>
    [SliderProperty("Max Dead Zone", 0f, 20f, 4f), Unit("px")]
    public float MaxDeadZone { get; set; } = 4f;

    /// <summary>Net speed at which the dead zone reaches zero and output becomes raw passthrough.</summary>
    [SliderProperty("Full Speed Threshold", 0.5f, 50f, 6f), Unit("px/ms")]
    public float FullSpeedThreshold { get; set; } = 6f;

    /// <summary>Dead-zone decay shape. &lt;1 collapses earlier, &gt;1 holds the zone longer.</summary>
    [SliderProperty("Curve", 0.1f, 3f, 1f)]
    public float Curve { get; set; } = 1f;

    /// <summary>Time constant of the velocity estimator. Higher reacts more slowly but more steadily.</summary>
    [SliderProperty("Velocity Smooth", 0f, 20f, 4f), Unit("ms")]
    public float VelocitySmoothMs { get; set; } = 4f;

    /// <summary>Optional extra output smoothing. Off by default; the dead zone alone is already continuous.</summary>
    [SliderProperty("Output Smooth", 0f, 8f, 0f), Unit("ms")]
    public float OutputSmoothMs { get; set; } = 0f;

    /// <summary>How much of the dead-zone offset to cancel on coherent movement. 0 = off, 1 = all of it.</summary>
    [SliderProperty("Lead", 0f, 1f, 0.75f)]
    public float Lead { get; set; } = 0.75f;

    /// <summary>How far directed movement shrinks the dead zone regardless of speed. 0 = v2.0 behaviour.</summary>
    [SliderProperty("Coherence Relief", 0f, 1f, 0.75f)]
    public float CoherenceRelief { get; set; } = 0.75f;

    // ─── Pipeline position ──────────────────────────────────────────────────

    /// <summary>
    /// PostTransform: positions arrive already mapped to screen pixels, which is why every setting
    /// above is in px or px/ms. Changing tablet area or resolution therefore changes what these
    /// values mean in physical terms and calls for retuning.
    /// </summary>
    public PipelinePosition Position => PipelinePosition.PostTransform;

    public event Action<IDeviceReport>? Emit;

    // ─── Internal state ─────────────────────────────────────────────────────

    private readonly VelocityGuardCore _core = new();
    private long _lastTimestamp;
    private bool _hasTimestamp;

    // ─── Pipeline entry point ───────────────────────────────────────────────

    public void Consume(IDeviceReport report)
    {
        if (report is ITabletReport tabletReport)
            tabletReport.Position = Filter(tabletReport.Position);

        Emit?.Invoke(report);
    }

    private Vector2 Filter(Vector2 input)
    {
        // Stopwatch rather than DateTime: reports arrive every 2-8 ms, well inside DateTime's
        // ~15 ms granularity, which would quantise the velocity estimate into uselessness.
        long now = Stopwatch.GetTimestamp();

        // A first report has no meaningful delta. Handing the core a non-positive dt makes it
        // reseed and pass through, which is exactly the wanted behaviour.
        float dtMs = 0f;
        if (_hasTimestamp)
            dtMs = (float)(now - _lastTimestamp) / Stopwatch.Frequency * 1000f;

        _lastTimestamp = now;
        _hasTimestamp = true;

        // Pen lifts and daemon stalls are recovered from the timestamp gap alone
        // (see VelocityGuardCore.ResetThresholdMs) rather than from proximity reports,
        // which keeps this shell free of optional driver report types.
        var settings = new VelocityGuardSettings
        {
            MaxDeadZone = MaxDeadZone,
            FullSpeedThreshold = FullSpeedThreshold,
            Curve = Curve,
            VelocitySmoothMs = VelocitySmoothMs,
            OutputSmoothMs = OutputSmoothMs,
            Lead = Lead,
            CoherenceRelief = CoherenceRelief
        };

        return _core.Filter(input, dtMs, in settings);
    }
}
