# Implementation notes

Things worth knowing before changing the code, and the numbers behind the claims in the README.
You do not need any of this to use the widget.

## Rate calculation

Counting inside a fixed time window makes the displayed value `keys in window × (60s / window length)`,
so it can only ever land on multiples of that factor — a 3-second window gives multiples of 20, which
means **the last digit is always 0**.

So the window is gone. Each keystroke does `rate += 1`; time decays it with `rate *= exp(-Δt/τ)`; the
display is `rate × 60000 / τ`. The value moves continuously and recent keystrokes weigh more.
τ defaults to 1.2s and is adjustable from the right-click menu.

O(1) time, O(1) memory, no quantisation. There is no better fit for this job.

## The hook

### It does not block input (measured)

The `WH_KEYBOARD_LL` callback forwards whatever it receives through `CallNextHookEx`, unconditionally.
Swallowing a key would require returning `1`, and no such path exists. Rather than trust the code alone,
this was measured — a control hook was installed **before** the widget so it sat behind the widget in
the chain, then 300 keystrokes were injected:

```
widget ABSENT   sent 300   reached the far end of the chain 300   lost 0
widget AHEAD    sent 300   reached the far end of the chain 300   lost 0
```

### It dies silently

Windows removes a low-level hook from the chain **without any notification** if its callback exceeds
`LowLevelHooksTimeout` (300ms by default). This actually reproduced during testing: the widget counted
9 of ~130 keystrokes and then quietly stopped. Hence the re-arm every 20 seconds. A freshly installed
hook also takes the front of the chain, which helps when another program grabs keys first.

### Raw Input is the structurally better fit

`WH_KEYBOARD_LL` means **the kernel hands every keystroke to this process and waits for a reply**.
Measured per-key latency goes from a median of 746µs to 860µs — about **+0.1ms**. That is not the cost
of the computation (26ns); it is the cost of sitting in the input path at all.

A program that only counts has no business being in that path. `RegisterRawInputDevices` with
`RIDEV_INPUTSINK` delivers `WM_INPUT` **asynchronously**, so it structurally cannot delay or swallow a
key, and there is no timeout removal — the 20-second re-arm would become unnecessary.
Feasibility is confirmed: in testing, Raw Input saw 401 of 400 injected keystrokes.

**One thing is unverified**: that test used `SendInput` injection, not input arriving over RDP.
Whether Raw Input sees Remote Desktop typing has to be checked on a real remote session, and if it
does not, this change must not be made.

## Measured performance

Everything the callback does per keystroke (the decay `exp`, the time bucket, the counters), repeated
2,000,000 times:

| | |
|---|---|
| Per keystroke | 26.1 ns (38,000 keys before it adds up to 1ms) |
| At 800 keys/min | 0.000035 % of one core |
| Managed heap growth over 1,000,000 keys | 0 bytes |

Whole process (20 cores, the convention Task Manager uses):

| State | CPU |
|---|---|
| Idle | 0.0000 % |
| 8 keys/sec | 0.023 % |
| 15 keys/sec | 0.008 % |

Memory over a 4-minute idle watch climbed from 27.3MB to 27.9MB private, then **flattened and gave
some back at 27.8MB**. GDI objects held at 29, handles at 281. No leak. The absolute figure is almost
entirely the .NET WinForms runtime; the widget's own data is under 100KB all told.

What keeps CPU low:

- Tallying happens only in the hook, so with no input no code runs at all
- The screen redraws 5 times a second, and **skips entirely when the content matches the last frame**
- When fully idle the timer drops to 1 second; the hook restores it the instant a key arrives
- GDI+ objects (fonts/pens/brushes/paths) are rebuilt only when the size changes, and colour changes
  mutate `.Color` rather than allocating new ones

## The cat sprite

The eleven images in `src/` (`1.png`–`9.png` running, plus `sitting.png` and `sleep.png`) are scaled to
32×32, joined horizontally, and embedded in the source as base64 so the build stays a single exe.
To change the artwork, re-run `tools/make_sprite.ps1` and swap the string.

**Only the outer background is knocked out.** Removing it by colour distance also takes the cream of the
cat's chest and paws. A flood fill seeded from the border pixels clears only what is reachable from
outside, so interior cream survives.

**Every 32×32 tile has 7 empty rows top and bottom and 2 empty columns either side.** Drawing only the
smallest rectangle containing all eleven frames (`x 1..30, y 3..27`) keeps the cat the same size while
taking 14px off the widget's height. Two traps:

- Cropping per frame kills the up-and-down bounce. The rectangle must be **shared across all frames**
- The bound must be computed at **alpha > 0**. Using something like 24 shaves off a faint edge column

On screen it is drawn at 2× as 60×50. Pixel art needs `NearestNeighbor`, and without
`WrapMode.TileFlipXY` the sampler pulls pixels from the adjacent frame and leaves a thin seam.

**One frame every 0.5 seconds.** Tying frame rate to typing speed makes it outrun the 5/sec repaint in
places, and the skipped frames read as jerky.

## Layout

Sizes are defined only as scale-1.0 constants (`BASE_*`), multiplied by DPI and the user's scale.
Default is 208×71.

The number field is sized for four digits: `9999` measures 67.6px in Consolas Bold 26px, so the field is
70px. Monospace keeps the width steady as digits change, and right alignment keeps the ones digit in
place. The gap to the left of shorter numbers is that four-digit reservation — removing it would make
the window width change in real time and visibly jitter.

## Appearance

Imitates the **vacuum fluorescent display** of an old taxi meter. GDI+ has no blur, so the glow is faked:
the number is drawn four times at ±1px offsets in a faint colour and then overdrawn in its real colour;
the graph gets one thick faint stroke with the real line on top.

A gradient brush's rectangle **must match the area actually filled**. Build it 1px wide and GDI+ tiles
it hundreds of times across, producing circular artifacts.

## Build gotchas

The legacy compiler (csc.exe, .NET Framework 4.8) accepts **C# 5 only**. No string interpolation, no
`?.`, no expression-bodied members, no `nameof`, no `out var`, no auto-property initialisers.

`build.ps1` is ASCII-only. PowerShell 5.1 reads BOM-less UTF-8 as ANSI, so non-ASCII characters in the
script break parsing.

## Screenshots and recording

The widget is a translucent layered window, so `Graphics.CopyFromScreen` will not capture it properly.
Use `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)`.

**The capturing process must be DPI-aware too.** On a 125% monitor, a non-DPI-aware process calling
`GetWindowRect` gets coordinates divided by 1.25 — a genuinely 221×75 window reads as 177×60, and the
capture comes out shrunk, making the layout look broken when it is fine.

When recording, encoding each frame to PNG inline **overruns the frame budget** and the timing drifts.
Collect bitmaps in memory and write them all at the end. And drive synthetic input from the **integral of
elapsed time**, not from slack time inside the frame loop — with no slack, no keys are ever sent.
