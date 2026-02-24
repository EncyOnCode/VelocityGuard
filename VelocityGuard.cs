using System;
using System.Diagnostics;
using System.Numerics;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Tablet;

namespace VelocityGuard;

/// <summary>
/// VelocityGuard v2 — Velocity-Adaptive Dead Zone filter for OpenTabletDriver.
/// Optimized for osu!: suppresses pen chatter at rest, passes fast movements with zero latency.
/// Features: high-precision timer, EMA speed smoothing, nonlinear curve, micro-smoothing, prediction.
/// </summary>
[PluginName("VelocityGuard")]
public class VelocityGuard : IPositionedPipelineElement<IDeviceReport>
{
    // ─── User-facing settings ────────────────────────────────────────────────

    /// <summary>Maximum dead-zone radius (screen pixels) when the pen is fully at rest.</summary>
    [SliderProperty("Max Dead Zone", 0f, 20f, 4f), Unit("px")]
    public float MaxDeadZone { get; set; } = 4f;

    /// <summary>Speed (px/ms) at which the dead zone shrinks to zero.</summary>
    [SliderProperty("Full Speed Threshold", 1f, 50f, 12f), Unit("px/ms")]
    public float FullSpeedThreshold { get; set; } = 12f;

    /// <summary>Dead zone decay curve. &lt;1 = collapses faster, &gt;1 = holds longer.</summary>
    [SliderProperty("Curve", 0.1f, 3f, 1f)]
    public float Curve { get; set; } = 1f;

    /// <summary>EMA alpha for speed smoothing. 1.0 = raw speed, lower = smoother.</summary>
    [SliderProperty("Speed Smooth", 0f, 1f, 0.5f)]
    public float SpeedSmoothAlpha { get; set; } = 0.5f;

    /// <summary>Interpolation factor at low speed. 1.0 = no smoothing, lower = smoother transitions.</summary>
    [SliderProperty("Smooth Factor", 0f, 1f, 0.8f)]
    public float MinSmoothFactor { get; set; } = 0.8f;

    /// <summary>Prediction strength along movement direction. 0 = off.</summary>
    [SliderProperty("Prediction", 0f, 2f, 0f)]
    public float PredictionStrength { get; set; } = 0f;

    // ─── Pipeline position ──────────────────────────────────────────────────

    public PipelinePosition Position => PipelinePosition.PostTransform;

    public event Action<IDeviceReport>? Emit;

    // ─── Internal state ─────────────────────────────────────────────────────

    private Vector2 _lastOutput;
    private Vector2 _lastInput;
    private long _lastTimestamp = Stopwatch.GetTimestamp();
    private float _smoothSpeed;
    private bool _initialized;

    // ─── Pipeline entry point ───────────────────────────────────────────────

    public void Consume(IDeviceReport report)
    {
        if (report is ITabletReport tabletReport)
        {
            tabletReport.Position = Filter(tabletReport.Position);
        }
        Emit?.Invoke(report);
    }

    // ─── Core filter logic ──────────────────────────────────────────────────

    private Vector2 Filter(Vector2 input)
    {
        // ── 1. High-precision delta time (Stopwatch instead of DateTime) ────
        long now = Stopwatch.GetTimestamp();
        float dt = (float)(now - _lastTimestamp) / Stopwatch.Frequency * 1000f; // ms
        _lastTimestamp = now;

        // First call: initialize state, pass through
        if (!_initialized)
        {
            _initialized = true;
            _lastInput = input;
            _lastOutput = input;
            return input;
        }

        // ── 2. Raw speed ────────────────────────────────────────────────────
        float rawSpeed = dt > 0.001f ? (input - _lastInput).Length() / dt : 0f;

        // ── 3. EMA speed smoothing ──────────────────────────────────────────
        float alpha = Math.Clamp(SpeedSmoothAlpha, 0.001f, 1f);
        _smoothSpeed = alpha * rawSpeed + (1f - alpha) * _smoothSpeed;

        Vector2 prevInput = _lastInput;
        _lastInput = input;

        // ── 4. Nonlinear dead zone curve ────────────────────────────────────
        float t = Math.Clamp(_smoothSpeed / Math.Max(FullSpeedThreshold, 0.001f), 0f, 1f);
        float shaped = MathF.Pow(t, Math.Max(Curve, 0.01f));
        float deadZone = MaxDeadZone * (1f - shaped);

        // ── 5. Prediction (shift input along movement direction) ────────────
        Vector2 effectiveInput = input;
        if (PredictionStrength > 0f && dt > 0.001f)
        {
            Vector2 delta = input - prevInput;
            float len = delta.Length();
            if (len > 0.001f)
            {
                Vector2 direction = delta / len; // normalized
                effectiveInput = input + direction * PredictionStrength * _smoothSpeed * dt;
            }
        }

        // ── 6. Dead zone gate + micro-smoothing ─────────────────────────────
        float dist = Vector2.Distance(effectiveInput, _lastOutput);

        if (dist > deadZone)
        {
            // Outside dead zone — update output
            // Micro-smoothing: lerp factor scales with speed (shaped)
            float smoothFactor = MathF.Max(shaped, Math.Clamp(MinSmoothFactor, 0.01f, 1f));
            _lastOutput = Vector2.Lerp(_lastOutput, effectiveInput, smoothFactor);
        }
        // else: inside dead zone — hold last output (chatter suppressed)

        return _lastOutput;
    }
}
