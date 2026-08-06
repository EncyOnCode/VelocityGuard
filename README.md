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
curve      = ln(1 − ZoneAtKneeSpeed) / ln(KneeSpeed / FullSpeedThreshold)
relief     = 1 − CoherenceRelief · coherence³            ← direction opens the zone too
deadZone   = MaxDeadZone · (1 − t^curve) · relief

lead       = direction(velocity) · deadZone · Lead · coherence³
target     = |input + lead − output| > deadZone
             ? (input + lead) − direction · deadZone     ← trail by exactly deadZone
             : output                                    ← hold, chatter suppressed
```

Three properties matter most.

**Net speed, not gross speed.** Chatter oscillates about a point, so its velocity vectors cancel and `netSpeed` stays near zero no matter how violently the sensor jitters. Measuring the *magnitude of each step* instead — as v1 did — lets sustained chatter inflate the speed estimate and open the very dead zone meant to catch it.

**The dead zone drags rather than gates.** The output trails the pen by exactly `deadZone` along the direction of travel. At the boundary, `input − direction·deadZone` is identically the current output, so movement starts from zero instead of jumping. That single change is what removes the stair-stepping on slow aim and the jolt where the filter hands over to raw passthrough.

**Direction, not just speed, opens the zone.** Speed alone cannot tell a deliberate 2 px correction from 2 px of chatter — both are slow and small. Left at that, the zone imposes a fixed positional offset that a small movement never escapes: measured, a 1 px nudge delivered only 26% of itself, permanently. `CoherenceRelief` lets consistently-directed movement shrink the zone at any speed, which recovers small corrections without letting jitter through, because jitter has no consistent direction to begin with.

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
| Fraction of a deliberate 1 px correction reaching the output | 0.26 (v2.0) | **0.81** |
| Fraction of a deliberate 2 px correction reaching the output | 0.63 (v2.0) | **0.91** |
| Disagreement between 125 / 250 / 1000 Hz report rates | 1.38 px | **0.27 px** |
| Position error at 10 px/ms | 0 px | 0 px |
| Recovery after a pen lift | 139 px off | **exact** |

Reproduce with `dotnet test`; the same figures back the thresholds in [tests/VelocityGuard.Tests](tests/VelocityGuard.Tests).

## Parameters

| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **Max Dead Zone** | 4 px | 0–20 | Dead-zone radius when the pen shows no net movement |
| **Full Speed Threshold** | 6 px/ms | 0.5–50 | Net speed at which the dead zone reaches zero |
| **Knee Speed** | 3 px/ms | 0.1–25 | Net speed at which the dead zone is Zone At Knee Speed of Max Dead Zone |
| **Zone At Knee Speed** | 0.5 | 0.01–0.99 | How much of the dead zone is left at Knee Speed |
| **Velocity Smooth** | 4 ms | 0–20 | Time constant of the velocity estimate |
| **Output Smooth** | 0 ms | 0–8 | Optional extra smoothing; off by default |
| **Lead** | 0.75 | 0–1 | Fraction of the dead-zone offset cancelled on coherent movement |
| **Coherence Relief** | 0.75 | 0–1 | How far consistently-directed movement shrinks the dead zone, at any speed |

The dead zone is fixed at its two ends — `Max Dead Zone` at rest, exactly zero at `Full Speed Threshold`. **Knee Speed** and **Zone At Knee Speed** name one point in between that the curve must pass through, and that is what sets its shape: "at 8 px/ms I still want 40% of the zone" is `Knee Speed = 8`, `Zone At Knee Speed = 0.4`.

Reading them separately: Knee Speed moves the bend left or right, Zone At Knee Speed moves it up or down. Putting the knee close behind Full Speed keeps real smoothing alive through fast movement while the mid range pays only a fraction of the zone; putting it low collapses the zone early and hands almost everything through. `Zone At Knee Speed = 0.5` at half the Full Speed Threshold is a plain linear decay.

Settings are in **screen pixels**, because the filter runs at `PostTransform` where coordinates are already mapped to the display. Changing tablet area or resolution changes what these numbers mean physically — retune after either.

### Tuning

- **Chatter still getting through?** → raise `Max Dead Zone` (5–8 px)
- **Slow aim feels sticky?** → lower `Max Dead Zone` (2–3 px), or raise `Lead` towards 1.0
- **Jumps feel filtered?** → lower `Full Speed Threshold` (3–4 px/ms)
- **Snappier response?** → lower `Knee Speed` to around a quarter of `Full Speed Threshold`, or drop `Zone At Knee Speed` below 0.5
- **Streams come out completely unfiltered, but raising `Full Speed Threshold` over-smooths normal aim?** → raise `Full Speed Threshold` past your stream speed *and* raise `Knee Speed` close behind it. The zone then still has substance at stream speed while mid-speed aim keeps only a small fraction of it — which a single "fully off" threshold could not express, since it forced everything below it to be smoothed proportionally harder.
- **Knee is at the right speed but the amount there is wrong?** → that is exactly what `Zone At Knee Speed` is for. Raise it towards 0.9 to hold almost the whole zone up to `Knee Speed` and then drop it sharply; lower it towards 0.1 to shed most of the zone early and trail the rest off gently. Neither end changes where the filter turns off entirely — that stays `Full Speed Threshold`.
- **Cursor trails behind?** → raise `Lead`. Unlike a conventional prediction term it is bounded by the dead zone, so it cannot overshoot on direction changes, and it switches itself off when movement stops being coherent.
- **Small corrections not registering?** → raise `Coherence Relief` towards 1.0. At 1.0 the zone collapses entirely on movement it judges fully coherent, so nothing deliberate is lost; the default keeps a quarter of the zone in reserve, because real chatter can have a preferred direction and partly pass for intent.
- **`Output Smooth` is off by default on purpose.** Against synthetic chatter it produced no measurable smoothness gain while costing tracking lag and report-rate consistency. It remains as an escape hatch for hardware whose noise the dead zone alone does not settle.

### Presets

| Parameter | Jumps | Streams | All-round |
|-----------|-------|---------|-----------|
| Max Dead Zone | 3 px | 5 px | 4 px |
| Full Speed Threshold | 4 px/ms | 8 px/ms | 6 px/ms |
| Knee Speed | 1.3 px/ms | 4.5 px/ms | 3 px/ms |
| Zone At Knee Speed | 0.5 | 0.5 | 0.5 |
| Velocity Smooth | 4 ms | 6 ms | 4 ms |
| Output Smooth | 0 ms | 0 ms | 0 ms |
| Lead | 0.75 | 0.5 | 0.75 |
| Coherence Relief | 0.75 | 0.6 | 0.75 |

Scaled for area and chatter severity by the [settings calculator](https://encyoncode.github.io/VelocityGuard/), which also runs both filter versions side by side on your own cursor movement.

## Upgrading from v1

**v1 settings do not carry over and will reset to the defaults above.** Three parameters changed both units and meaning, so keeping their names would have silently reinterpreted saved values instead of resetting them:

| v1 | v2 | What changed |
|----|----|--------------|
| `Speed Smooth` (0–1, α per report) | `Velocity Smooth` (0–20 ms) | Now a time constant, so behaviour no longer depends on report rate |
| `Smooth Factor` (0–1, higher = *less* smoothing) | `Output Smooth` (0–8 ms, higher = *more*) | Meaning inverted; defaults off |
| `Prediction` (0–2, scaled by speed) | `Lead` (0–1, scaled by dead zone) | Bounded and coherence-gated; the old form overshot at high speed |

`Full Speed Threshold` keeps its name but now measures net rather than gross speed, and wants **roughly half** its old value.

## Upgrading from v2.0–v2.2

`Curve` is gone. The decay shape is now stated as a point the curve passes through — `Knee Speed` and `Zone At Knee Speed` — rather than as a raw exponent, because the exponent could not express what the filter was actually being asked for: keeping smoothing alive through fast streaming while leaving mid-speed aim light needs values around 3 and above, which sat outside the old slider's range and meant nothing as numbers.

**Only these shape settings reset; everything else carries over.** They are renames rather than reused keys, so a saved `Curve` of 0.6 is never read back as 0.6 px/ms.

| Coming from | What to enter |
|---|---|
| v2.0 / v2.1 `Curve` | `Knee Speed = FullSpeedThreshold · 0.5^(1/Curve)`, `Zone At Knee Speed = 0.5` |
| v2.2 `Half Speed Threshold` | `Knee Speed` = the same number, `Zone At Knee Speed = 0.5` |

Defaults are unchanged in behaviour throughout: `Curve = 1.0` at `Full Speed Threshold = 6` is `Knee Speed = 3` at `Zone At Knee Speed = 0.5`.

v2.2 named this knob `Half Speed Threshold`, which stopped being true once the fraction became tunable — hence one further rename, to `Knee Speed`, rather than a key called "half speed" that is not a half speed.

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
