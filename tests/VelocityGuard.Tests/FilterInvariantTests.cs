using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace VelocityGuard.Tests;

/// <summary>
/// Invariants rather than golden values: there is no reference number for how a filter should
/// "feel", but there are properties it must hold. Every threshold below is set well clear of both
/// the measured v2 behaviour and the v1 behaviour it replaces, so these fail loudly on regression
/// without being sensitive to float rounding.
/// </summary>
public class FilterInvariantTests
{
    // ── 1. The whole point of the filter: no added latency once the pen is moving fast ──
    [Fact]
    public void FastMovement_PassesThroughExactly()
    {
        var core = new VelocityGuardCore();
        var settings = Signals.Baseline;
        const float dtMs = 4f, stepPx = 40f; // 10 px/ms, well past the 4 px/ms threshold

        float worst = 0f;
        for (int i = 0; i < 200; i++)
        {
            var input = new Vector2(500f + i * stepPx, 500f);
            var output = core.Filter(input, dtMs, in settings);
            if (i > 50) worst = MathF.Max(worst, Vector2.Distance(output, input));
        }

        // Exactly zero, not merely small: the dead zone collapses to 0 and smoothing is bypassed,
        // so the core assigns the input outright rather than interpolating towards it.
        Assert.Equal(0f, worst);
    }

    // ── 2. Chatter must not open the dead zone it is supposed to be caught by ──
    // Bounds sit far above the measured v2 figure (right-hand column) and far below what the v1
    // gate scores on the same signal (left), so they catch regressions without being seed-sensitive.
    //                     amplitude  bound     v1        v2
    [Theory]
    [InlineData(1.0f, 0.5f)]   //     0.0       0.0     — fully inside the dead zone, must be dead still
    [InlineData(1.5f, 5f)]     //   208-327   0.0-0.7
    [InlineData(2.0f, 100f)]   //   930-994    30-32
    [InlineData(3.0f, 900f)]   // 2088-2146   587-620   — amplitude ~ dead zone; some leak is unavoidable
    public void StationaryPen_LeaksLittleMovement(float amplitude, float maxLeak)
    {
        float leak = Signals.ChatterLeak(new VelocityGuardCore(), Signals.Baseline, amplitude);
        Assert.InRange(leak, 0f, maxLeak);
    }

    // ── 3. Behaviour must not depend on how fast the tablet reports ──
    [Fact]
    public void SameGesture_AgreesAcrossReportRates()
    {
        var settings = Signals.Baseline;
        const float durationMs = 600f;

        var at125 = Signals.SampleGesture(in settings, 125f, durationMs);
        var at250 = Signals.SampleGesture(in settings, 250f, durationMs);
        var at1000 = Signals.SampleGesture(in settings, 1000f, durationMs);

        // All three rates land a sample on every 8 ms boundary, so compare those directly.
        // Comparing "latest sample before t" would fold in sample-and-hold error instead.
        float worst = 0f;
        for (int i = 12; i * 8 <= durationMs && 8 * i < at1000.Count; i++)
            worst = MathF.Max(worst,
                MathF.Max(Vector2.Distance(at125[i], at250[2 * i]),
                          Vector2.Distance(at250[2 * i], at1000[8 * i])));

        Assert.InRange(worst, 0f, 0.75f); // measured ~0.27 px; v1's gate scores ~1.38 px
    }

    // ── 4. Slow deliberate movement must not come out as freeze-then-jump ──
    [Fact]
    public void SlowMovement_ProducesNoStairStepping()
    {
        var core = new VelocityGuardCore();
        var settings = Signals.Baseline;
        const float dtMs = 4f, stepPx = 0.25f; // 0.0625 px/ms — slow aim

        Vector2? previous = null;
        float largestStep = 0f;
        for (int i = 0; i < 400; i++)
        {
            var output = core.Filter(new Vector2(500f + i * stepPx, 500f), dtMs, in settings);
            if (i > 100 && previous.HasValue)
                largestStep = MathF.Max(largestStep, Vector2.Distance(output, previous.Value));
            previous = output;
        }

        // The output advances by the same increment as the input. A gate would sit still and then
        // jump the width of the dead zone, scoring an order of magnitude higher.
        Assert.InRange(largestStep / stepPx, 0f, 1.05f);
    }

    // ── 5. A stale timestamp must not be read as "the pen moved very slowly" ──
    [Fact]
    public void PenLift_ReacquiresAtNewPosition()
    {
        var core = new VelocityGuardCore();
        var settings = Signals.Baseline;

        for (int i = 0; i < 100; i++)
            core.Filter(new Vector2(200f + i, 200f), 4f, in settings);

        // Pen leaves the surface, returns 5 s later somewhere else entirely.
        var reacquired = core.Filter(new Vector2(1500f, 900f), 5000f, in settings);

        Assert.Equal(new Vector2(1500f, 900f), reacquired);
    }

    [Fact]
    public void FirstReport_PassesThrough()
    {
        var core = new VelocityGuardCore();
        var settings = Signals.Baseline;
        Assert.Equal(new Vector2(123f, 456f), core.Filter(new Vector2(123f, 456f), 0f, in settings));
    }

    // ── 6. No parameter combination or timing may produce a non-finite cursor position ──
    [Fact]
    public void ExtremeInputs_NeverProduceNonFiniteOutput()
    {
        var prng = new Signals.Prng(99);
        float[] deltas = { 0f, 1e-9f, 0.001f, 4f, 1e6f, float.PositiveInfinity, float.NaN };

        for (int trial = 0; trial < 2000; trial++)
        {
            var settings = new VelocityGuardSettings
            {
                MaxDeadZone = prng.Next(0f, 20f),
                FullSpeedThreshold = prng.Next(0.5f, 50f),
                // Deliberately past the slider's 0.1–25: a half speed at or above the full-speed
                // threshold, or at zero, both drive the derived exponent to an infinity if unclamped.
                HalfSpeedThreshold = prng.Next(0f, 60f),
                VelocitySmoothMs = prng.Next(0f, 20f),
                OutputSmoothMs = prng.Next(0f, 8f),
                Lead = prng.Next(0f, 1f)
            };

            var core = new VelocityGuardCore();
            for (int i = 0; i < 40; i++)
            {
                float dtMs = deltas[(int)MathF.Abs(prng.Next(0f, deltas.Length - 0.001f))];
                var output = core.Filter(new Vector2(prng.Next(0f, 4000f), prng.Next(0f, 4000f)), dtMs, in settings);
                Assert.True(float.IsFinite(output.X) && float.IsFinite(output.Y),
                    $"non-finite output {output} at trial {trial}, step {i}, dt {dtMs}");
            }
        }
    }

    [Fact]
    public void NonFiniteInput_IsIgnoredRatherThanStored()
    {
        var core = new VelocityGuardCore();
        var settings = Signals.Baseline;

        for (int i = 0; i < 50; i++)
            core.Filter(new Vector2(300f + i, 300f), 4f, in settings);

        var held = core.Filter(new Vector2(float.NaN, 300f), 4f, in settings);
        Assert.True(float.IsFinite(held.X) && float.IsFinite(held.Y));

        // State must survive the bad sample intact.
        var next = core.Filter(new Vector2(360f, 300f), 4f, in settings);
        Assert.True(float.IsFinite(next.X) && float.IsFinite(next.Y));
    }

    // ── 7. Lead exists to cancel dead-zone lag, and must not become a chatter amplifier ──
    [Fact]
    public void Lead_DoesNotAmplifyChatter()
    {
        var off = Signals.Baseline; off.Lead = 0f;
        var on = Signals.Baseline; on.Lead = 1f;

        float leakOff = Signals.ChatterLeak(new VelocityGuardCore(), in off, 1.5f);
        float leakOn = Signals.ChatterLeak(new VelocityGuardCore(), in on, 1.5f);

        // v1's prediction scored roughly 10x its own baseline here.
        Assert.InRange(leakOn, 0f, MathF.Max(leakOff, 0.05f) * 1.5f);
    }

    [Fact]
    public void Lead_ReducesTrackingLagAtEverySpeed()
    {
        foreach (float pxPerMs in new[] { 0.25f, 0.5f, 1f, 2f, 3f })
        {
            float lagWithout = SteadyLag(0f, pxPerMs);
            float lagWith = SteadyLag(1f, pxPerMs);

            // Must help, and must never overshoot into being worse than doing nothing.
            Assert.True(lagWith <= lagWithout + 0.01f,
                $"lead increased lag at {pxPerMs} px/ms: {lagWithout} -> {lagWith}");
        }

        static float SteadyLag(float lead, float pxPerMs)
        {
            var core = new VelocityGuardCore();
            var settings = Signals.Baseline;
            settings.Lead = lead;

            const float dtMs = 4f;
            float step = pxPerMs * dtMs, worst = 0f;
            for (int i = 0; i < 500; i++)
            {
                var input = new Vector2(500f + i * step, 500f);
                var output = core.Filter(input, dtMs, in settings);
                if (i > 400) worst = MathF.Max(worst, Vector2.Distance(output, input));
            }
            return worst;
        }
    }

    // ── A deliberate correction must survive the dead zone, however small it is ──
    // Speed and amplitude cannot separate a 2 px correction from 2 px of chatter, so before
    // CoherenceRelief the zone left a permanent offset of DeadZone * (1 - Lead) that small
    // movements never escaped. Direction is what separates them.
    //                     distance  floor    relief=0   relief=0.75
    [Theory]
    [InlineData(1f, 0.70f)]   //     0.26       0.81
    [InlineData(2f, 0.85f)]   //     0.63       0.91
    [InlineData(3f, 0.88f)]   //     0.75       0.94
    [InlineData(10f, 0.95f)]  //     0.93       0.98
    public void SmallDeliberateMovement_ReachesTheOutput(float distancePx, float minSurvival)
    {
        float survival = Signals.MicroMovementSurvival(Signals.Baseline, distancePx);
        Assert.InRange(survival, minSurvival, 1.05f);
    }

    [Fact]
    public void CoherenceRelief_DoesNotUndoChatterSuppression()
    {
        var without = Signals.Baseline; without.CoherenceRelief = 0f;
        var with = Signals.Baseline; with.CoherenceRelief = 1f;

        // Relief keys off direction, and chatter has none — so opening the zone for coherent
        // movement must not open it for jitter. Measured, relief actually improves this case.
        float leakWithout = Signals.ChatterLeak(new VelocityGuardCore(), in without, 1.5f);
        float leakWith = Signals.ChatterLeak(new VelocityGuardCore(), in with, 1.5f);

        Assert.InRange(leakWith, 0f, MathF.Max(leakWithout, 0.05f) * 1.5f);
    }

    // ── The behaviour that motivated v2: slow aim with tremor riding on it ──
    [Fact]
    public void SlowAimWithTremor_TracksNearlyStraight()
    {
        var core = new VelocityGuardCore();
        var settings = Signals.Baseline;
        var prng = new Signals.Prng(7);

        const float dtMs = 4f, driftPxPerMs = 0.05f, tremor = 2f;
        const int samples = 800;

        var output = new List<Vector2>();
        for (int i = 0; i < samples; i++)
        {
            var input = new Vector2(500f + i * driftPxPerMs * dtMs + prng.NextUnit() * tremor,
                                    500f + prng.NextUnit() * tremor);
            var result = core.Filter(input, dtMs, in settings);
            if (i > 100) output.Add(result);
        }

        float idealPath = (samples - 101) * driftPxPerMs * dtMs;
        float excess = Signals.PathLength(output) / idealPath;

        // Measured across seeds: v1 travels 4.40-5.11x the intended distance here, v2 1.16-1.21x.
        Assert.InRange(excess, 0f, 1.6f);
    }

    // ── The knob must mean literally what its name says ──
    // Half Speed Threshold replaced the raw Curve exponent so the number could be read directly off
    // a movement speed. That only holds if the zone really is half-height at exactly that speed.
    [Theory]
    [InlineData(1f)]
    [InlineData(3f)]
    [InlineData(5f)]
    [InlineData(9f)]
    public void AtHalfSpeedThreshold_DeadZoneIsHalfOfMax(float halfSpeed)
    {
        var settings = Signals.Baseline;
        settings.MaxDeadZone = 4f;
        settings.FullSpeedThreshold = 10f;
        settings.HalfSpeedThreshold = halfSpeed;
        settings.CoherenceRelief = 0f; // steady motion is fully coherent; relief would scale the result

        Assert.Equal(2f, SteadyDeadZone(in settings, halfSpeed), 2);
    }

    // Raising the half speed must hold the zone open longer at every speed below the threshold —
    // this is the lever for keeping smoothing alive through fast movement without over-smoothing
    // the mid range, which a single "fully off" threshold could not offer.
    [Fact]
    public void RaisingHalfSpeed_HoldsTheZoneOpenLongerButStillPassesThroughAtFullSpeed()
    {
        var settings = Signals.Baseline;
        settings.MaxDeadZone = 4f;
        settings.FullSpeedThreshold = 10f;
        settings.CoherenceRelief = 0f;

        float previous = -1f;
        foreach (float halfSpeed in new[] { 1f, 2f, 3f, 5f, 7f, 9f })
        {
            settings.HalfSpeedThreshold = halfSpeed;

            float zone = SteadyDeadZone(in settings, 6f);
            Assert.True(zone > previous, $"zone at 6 px/ms did not grow at half speed {halfSpeed}");
            previous = zone;

            // Whatever the shape, the zone still reaches exactly zero at the threshold, which is
            // what keeps fast movement bit-exact.
            Assert.Equal(0f, SteadyDeadZone(in settings, settings.FullSpeedThreshold));
        }
    }

    /// <summary>Dead-zone radius once the velocity estimator has settled on a steady straight-line speed.</summary>
    private static float SteadyDeadZone(in VelocityGuardSettings settings, float pxPerMs)
    {
        var core = new VelocityGuardCore();
        const float dtMs = 4f;
        float step = pxPerMs * dtMs;

        for (int i = 0; i < 400; i++)
            core.Filter(new Vector2(500f + i * step, 500f), dtMs, in settings);

        return core.DeadZone;
    }
}
