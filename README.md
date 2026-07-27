# VelocityGuard

OpenTabletDriver filter plugin optimized for **osu!** — suppresses pen chatter while handing fast movement through with zero added latency.

## How it works

A dead zone whose radius shrinks as the pen moves, driven by how *coherent* the movement is rather than by raw speed:

```
v          = (input − lastInput) / Δt                    vector, px/ms
velocity   = velocity + (v − velocity) · (1 − e^(−Δt/τ)) time-normalised EMA
netSpeed   = |velocity|                                  ← cancels out chatter
coherence  = |EMA(v)| / EMA(|v|)                         0 = jitter, 1 = deliberate

t          = clamp(netSpeed / FullSpeedThreshold, 0, 1)
deadZone   = MaxDeadZone · (1 − t^Curve)

lead       = direction(velocity) · deadZone · Lead · coherence³
target     = |input + lead − output| > deadZone
             ? (input + lead) − direction · deadZone     ← trail by exactly deadZone
             : output                                    ← hold, chatter suppressed
```

Two properties matter most.

**Net speed, not gross speed.** Chatter oscillates about a point, so its velocity vectors cancel and `netSpeed` stays near zero no matter how violently the sensor jitters. Measuring the *magnitude of each step* instead — as v1 did — lets sustained chatter inflate the speed estimate and open the very dead zone meant to catch it.

**The dead zone drags rather than gates.** The output trails the pen by exactly `deadZone` along the direction of travel. At the boundary, `input − direction·deadZone` is identically the current output, so movement starts from zero instead of jumping. That single change is what removes the stair-stepping on slow aim and the jolt where the filter hands over to raw passthrough.

| Situation | Dead zone | Output |
|-----------|-----------|--------|
| Pen at rest, sensor jittering | = MaxDeadZone | Cursor completely still |
| Slow deliberate aim | Shrinking | Smooth, trailing by at most the dead zone |
| Fast jump or stream | = 0 px | **Bit-exact passthrough, zero added latency** |

## Measured behaviour

Both versions driven with identical synthetic input. Lower is better except where noted.

| | v1 | v2 |
|---|---|---|
| Cursor drift while pen held still, ±1.5 px sensor noise | 299 px | **0.18 px** |
| Slow aim with ±2 px tremor — path travelled vs. path intended | 4.4–5.1× | **1.16–1.21×** |
| Largest output step ÷ input step on slow movement | 11× | **1.0×** |
| Disagreement between 125 / 250 / 1000 Hz report rates | 1.38 px | **0.27 px** |
| Position error at 10 px/ms | 0 px | 0 px |
| Recovery after a pen lift | 139 px off | **exact** |

Reproduce with `dotnet test`; the same figures back the thresholds in [tests/VelocityGuard.Tests](tests/VelocityGuard.Tests).

## Parameters

| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **Max Dead Zone** | 4 px | 0–20 | Dead-zone radius when the pen shows no net movement |
| **Full Speed Threshold** | 6 px/ms | 0.5–50 | Net speed at which the dead zone reaches zero |
| **Curve** | 1.0 | 0.1–3.0 | Decay shape (<1 collapses earlier, >1 holds longer) |
| **Velocity Smooth** | 4 ms | 0–20 | Time constant of the velocity estimate |
| **Output Smooth** | 0 ms | 0–8 | Optional extra smoothing; off by default |
| **Lead** | 0.75 | 0–1 | Fraction of the dead-zone offset cancelled on coherent movement |

Settings are in **screen pixels**, because the filter runs at `PostTransform` where coordinates are already mapped to the display. Changing tablet area or resolution changes what these numbers mean physically — retune after either.

### Tuning

- **Chatter still getting through?** → raise `Max Dead Zone` (5–8 px)
- **Slow aim feels sticky?** → lower `Max Dead Zone` (2–3 px), or raise `Lead` towards 1.0
- **Jumps feel filtered?** → lower `Full Speed Threshold` (3–4 px/ms)
- **Snappier response?** → lower `Curve` (0.4–0.7)
- **Cursor trails behind?** → raise `Lead`. Unlike a conventional prediction term it is bounded by the dead zone, so it cannot overshoot on direction changes, and it switches itself off when movement stops being coherent.
- **`Output Smooth` is off by default on purpose.** Against synthetic chatter it produced no measurable smoothness gain while costing tracking lag and report-rate consistency. It remains as an escape hatch for hardware whose noise the dead zone alone does not settle.

### Presets

| Parameter | Jumps | Streams | All-round |
|-----------|-------|---------|-----------|
| Max Dead Zone | 3 px | 5 px | 4 px |
| Full Speed Threshold | 4 px/ms | 8 px/ms | 6 px/ms |
| Curve | 0.6 | 1.2 | 1.0 |
| Velocity Smooth | 4 ms | 6 ms | 4 ms |
| Output Smooth | 0 ms | 0 ms | 0 ms |
| Lead | 0.75 | 0.5 | 0.75 |

Scaled for area and chatter severity by the [settings calculator](https://encyoncode.github.io/VelocityGuard/), which also runs both filter versions side by side on your own cursor movement.

## Upgrading from v1

**v1 settings do not carry over and will reset to the defaults above.** Three parameters changed both units and meaning, so keeping their names would have silently reinterpreted saved values instead of resetting them:

| v1 | v2 | What changed |
|----|----|--------------|
| `Speed Smooth` (0–1, α per report) | `Velocity Smooth` (0–20 ms) | Now a time constant, so behaviour no longer depends on report rate |
| `Smooth Factor` (0–1, higher = *less* smoothing) | `Output Smooth` (0–8 ms, higher = *more*) | Meaning inverted; defaults off |
| `Prediction` (0–2, scaled by speed) | `Lead` (0–1, scaled by dead zone) | Bounded and coherence-gated; the old form overshot at high speed |

`Full Speed Threshold` keeps its name but now measures net rather than gross speed, and wants **roughly half** its old value.

## Comparison with other filters

| Plugin | Driven by | Latency when moving fast | Suppression during slow aim |
|--------|-----------|--------------------------|-----------------------------|
| CHATTER EXTERMINATOR | Fixed dead zone | None | Yes, but steps as the threshold is crossed |
| Devocub Antichatter | Smoothing + shrinking zone | Small | Partial |
| Radial Follow | Distance from cursor to pen | Always present by design | Smooth, but the cursor always trails |
| **VelocityGuard** | **Coherence of pen movement** | **None** | **Yes, without stepping** |

Worth being straight about what this does and does not buy:

- A plain fixed dead zone also has no latency on fast movement — it is measured from the cursor, so at speed the pen is always far outside it. VelocityGuard's advantage is in the middle: slow, deliberate aim, where a fixed zone stair-steps and this one does not.
- Radial Follow's constant trailing offset is not simply a defect. A predictable offset is something muscle memory adapts to, whereas a filter that treats movement differently by speed is inherently less consistent. VelocityGuard's answer is to make the transition continuous, but the tradeoff is real.
- The advantage grows with how noisy your tablet actually is. On clean hardware (±1 px), v2 trades roughly a pixel of extra lag during slow aim for a small smoothness gain — tune `Max Dead Zone` down to 2 px there. On noisy hardware (±2 px) it is not close.

## Installation

**Via the plugin manager:** Plugins → Open Plugin Manager → File → Use alternate source, with owner `EncyOnCode`, name `VelocityGuard`, ref `main`.

**Manually:**

1. Download `VelocityGuard.zip` from [Releases](../../releases)
2. Extract into:
   - **Windows:** `%localappdata%\OpenTabletDriver\Plugins\VelocityGuard\`
   - **Linux/macOS:** `~/.config/OpenTabletDriver/Plugins/VelocityGuard/`
3. Restart the OpenTabletDriver daemon
4. Filters tab → Add → **VelocityGuard**

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet build -c Release
```

Output: `bin/Release/VelocityGuard.dll`. Run the filter invariants with:

```bash
dotnet test tests/VelocityGuard.Tests/VelocityGuard.Tests.csproj
```

Releases are cut locally with `scripts/release.sh <version>`, which tests, builds, packages, fills in the plugin manifest's download URL and lowercase checksum, publishes, and then re-downloads the published asset to confirm its hash matches what the manifest claims.

## License

GPL-3.0
