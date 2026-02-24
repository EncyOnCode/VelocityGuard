# VelocityGuard

OpenTabletDriver filter plugin optimized for **osu!** — eliminates pen chatter with zero added latency on fast movements.

## How it works

**Velocity-Adaptive Dead Zone** — a dead zone that shrinks to zero as pen speed increases:

```
speed       = distance / deltaTime                              (px/ms)
smoothSpeed = α × rawSpeed + (1−α) × prevSmoothedSpeed          (EMA)
t           = clamp(smoothSpeed / FullSpeedThreshold, 0, 1)
shaped      = t ^ Curve
deadZone    = MaxDeadZone × (1 − shaped)

If distance(input, lastOutput) > deadZone:
    output = lerp(lastOutput, input, max(shaped, SmoothFactor))  ← update
Else:
    output = lastOutput                                          ← chatter suppressed
```

| Situation | Dead Zone | Output |
|-----------|-----------|--------|
| Pen at rest / slow hover | = MaxDeadZone | Chatter eliminated |
| Medium aim movement | Reduced | Smooth interpolated movement |
| Fast jump / stream | = 0 px | **Raw passthrough, zero added latency** |

## Features

- **Adaptive dead zone** — shrinks with pen speed, fully off during jumps
- **High-precision timing** — `Stopwatch` (~100 ns) instead of `DateTime` (~15 ms)
- **EMA speed smoothing** — prevents noise spikes from briefly disabling the dead zone
- **Nonlinear curve** — configurable dead zone decay shape
- **Micro-smoothing** — optional interpolation for smooth slow aim
- **Movement prediction** — optional look-ahead to reduce initial dead zone lag

## Parameters

| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **Max Dead Zone** | 4 px | 0–20 | Dead zone radius when pen is still |
| **Full Speed Threshold** | 12 px/ms | 1–50 | Speed at which dead zone = 0 |
| **Curve** | 1.0 | 0.1–3.0 | Decay shape (<1 = faster collapse, >1 = holds longer) |
| **Speed Smooth** | 0.5 | 0–1 | EMA alpha for speed (1.0 = raw, lower = smoother) |
| **Smooth Factor** | 0.8 | 0–1 | Interpolation on slow moves (1.0 = off, lower = smoother) |
| **Prediction** | 0.0 | 0–2 | Prediction strength (0 = off) |

### Tuning tips for osu!

- **More chatter?** → increase `Max Dead Zone` (5–8 px)
- **Slow aim feels sticky?** → decrease `Max Dead Zone` (2–3 px)
- **Jumps feel filtered?** → decrease `Full Speed Threshold` (6–8 px/ms)
- **Want snappier response?** → decrease `Curve` (0.4–0.7)
- **Want silky slow aim?** → decrease `Smooth Factor` (0.5–0.7)

### Recommended preset for fast jumps (medium area ~66×41 mm)

| Parameter | Value |
|-----------|-------|
| Max Dead Zone | 3 px |
| Full Speed Threshold | 6 px/ms |
| Curve | 0.6 |
| Speed Smooth | 0.6 |
| Smooth Factor | 0.9 |
| Prediction | 0.0 |

## Comparison with existing plugins

| Plugin | Mechanism | Latency | Adaptive |
|--------|-----------|---------|----------|
| CHATTER EXTERMINATOR | Fixed dead zone | ~0 | ❌ |
| Devocub Antichatter | Smoothing + shrinking zone | Small | Partial |
| Radial Follow | Tracking circle with EMA | Always present | ❌ |
| **VelocityGuard** | **Velocity-adaptive dead zone** | **0 on fast moves** | **✅** |

## Installation

1. Download `VelocityGuard.dll` from [Releases](../../releases)
2. Copy to:
   - **Windows:** `%localappdata%\OpenTabletDriver\Plugins\VelocityGuard\`
   - **Linux:** `~/.config/OpenTabletDriver/Plugins/VelocityGuard/`
3. Restart OpenTabletDriver daemon
4. Filters tab → Add → **VelocityGuard**

## Building from source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet build -c Release
```

Output: `bin/Release/VelocityGuard.dll`

## License

GPL-3.0
