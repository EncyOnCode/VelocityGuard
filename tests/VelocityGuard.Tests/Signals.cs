using System;
using System.Collections.Generic;
using System.Numerics;

namespace VelocityGuard.Tests;

/// <summary>
/// Synthetic pen input and the measurements taken over it. Everything here is deterministic so a
/// failure reproduces exactly; nothing depends on <see cref="Random"/>, whose algorithm is not
/// guaranteed stable across runtimes.
/// </summary>
internal static class Signals
{
    /// <summary>mulberry32 — small, fast, and identical across platforms and runtime versions.</summary>
    internal sealed class Prng
    {
        private uint _state;
        public Prng(uint seed) => _state = seed;

        public float NextUnit() // [-1, 1)
        {
            _state += 0x6D2B79F5u;
            uint z = _state;
            z = (z ^ (z >> 15)) * (z | 1u);
            z ^= z + (z ^ (z >> 7)) * (z | 61u);
            return ((z ^ (z >> 14)) / 4294967296f) * 2f - 1f;
        }

        public float Next(float min, float max) => min + (NextUnit() * 0.5f + 0.5f) * (max - min);
    }

    internal static VelocityGuardSettings Baseline => new()
    {
        MaxDeadZone = 3f,
        FullSpeedThreshold = 4f,
        // 4 * 0.5^(1/0.6) — the half-zone speed of the Curve = 0.6 baseline every threshold below
        // was measured against, so the recorded figures stay comparable across the reparameterisation.
        KneeSpeed = 1.259921f,
        ZoneAtKneeSpeed = 0.5f,
        VelocitySmoothMs = 4f,
        OutputSmoothMs = 0f,
        Lead = 0.75f,
        CoherenceRelief = 0.75f
    };

    /// <summary>
    /// A small deliberate correction made from rest, then held. Returns the fraction of it that
    /// reached the output — 1.0 means fully delivered, 0.0 means entirely swallowed by the dead zone.
    /// </summary>
    internal static float MicroMovementSurvival(in VelocityGuardSettings settings,
        float distancePx, float durationMs = 600f, float dtMs = 4f)
    {
        var core = new VelocityGuardCore();
        int steps = (int)(durationMs / dtMs);
        float step = distancePx / steps;

        var output = new Vector2(500f, 500f);
        for (int i = 0; i < 50; i++)
            output = core.Filter(new Vector2(500f, 500f), dtMs, in settings);

        float startX = output.X;
        for (int i = 1; i <= steps; i++)
            output = core.Filter(new Vector2(500f + i * step, 500f), dtMs, in settings);
        for (int i = 0; i < 50; i++)
            output = core.Filter(new Vector2(500f + distancePx, 500f), dtMs, in settings);

        return (output.X - startX) / distancePx;
    }

    /// <summary>Total distance travelled by a sequence of points.</summary>
    internal static float PathLength(IReadOnlyList<Vector2> points)
    {
        float total = 0f;
        for (int i = 1; i < points.Count; i++)
            total += Vector2.Distance(points[i], points[i - 1]);
        return total;
    }

    /// <summary>
    /// Pen held still while the sensor jitters. Returns how far the output travelled — the distance
    /// a user would see the cursor crawl while not moving their hand at all.
    /// </summary>
    internal static float ChatterLeak(VelocityGuardCore core, in VelocityGuardSettings settings,
        float amplitude, int samples = 1000, float dtMs = 4f)
    {
        var prng = new Prng(1234);
        var output = new List<Vector2>(samples);
        for (int i = 0; i < samples; i++)
        {
            var input = new Vector2(500f + prng.NextUnit() * amplitude, 500f + prng.NextUnit() * amplitude);
            var result = core.Filter(input, dtMs, in settings);
            if (i > 50) output.Add(result);
        }
        return PathLength(output);
    }

    /// <summary>A smooth two-frequency gesture, defined continuously so it can be sampled at any rate.</summary>
    internal static Vector2 Gesture(float tMs)
        => new(500f + 120f * MathF.Sin(tMs / 60f), 500f + 90f * MathF.Sin(tMs / 37f));

    internal static List<Vector2> SampleGesture(in VelocityGuardSettings settings, float hz, float durationMs)
    {
        var core = new VelocityGuardCore();
        float dtMs = 1000f / hz;
        var output = new List<Vector2>();
        for (float t = 0f; t <= durationMs; t += dtMs)
            output.Add(core.Filter(Gesture(t), dtMs, in settings));
        return output;
    }
}
