# GlideScroll Changelog

## v1.3.2

### Fixed: the settings window on scaled displays

- **The settings window now sizes itself correctly when Windows display scaling is above 100 percent.** It previously opened at its unscaled size while its contents rendered at your display scale, so at 125 percent everything was squeezed into a window a quarter too small: the tabs looked cramped and squarish, the Preferences panel scrolled instead of fitting on one page, and content near the bottom was cut off. The window now scales with your display the way every other app on your desktop does, and follows along if you drag it between monitors that use different scaling.
- Scrolling, panning, Game Mode, themes, and your saved settings are all untouched.

## v1.3.1

### Support GlideScroll, if you feel like it

- **A new SUPPORT panel in Preferences.** GlideScroll stays free and fully unlocked. If it earned it, "Open buy page" takes you to the pay-what-you-want page in your browser, and "Already supported" lets you enter your name and, optionally, your order number. The app takes your word for it: nothing is checked, nothing is sent anywhere, and the panel simply thanks you by name from then on.
- **The Preferences window is wider and better organized.** General settings and the new Support panel now sit side by side, so everything fits on screen without scrolling.
- This is the only change in this release. Scrolling, panning, Game Mode, and themes are untouched.

## v1.3

### Game Mode: GlideScroll now steps aside for your games

- **New Game Mode button on the App rules tab (the controller icon).** When enabled, GlideScroll pauses itself while the window you are scrolling belongs to a game, and resumes the instant you are back on a normal window. This keeps smoothing from interfering with in-game scrolling (weapon switching stays crisp) and keeps GlideScroll's synthetic input entirely clear of anti-cheat while a game is on screen.
- **Detection is layered and needs no upkeep.** A game is recognized by any of: a known game exe from the editable list below, anti-cheat runtime files shipped in the game's own install folder, an anti-cheat engine running (Easy Anti-Cheat, BattlEye, GameGuard, XIGNCODE, and others), or a borderless or exclusive fullscreen window corroborated by a live game. Engines are detected rather than individual games, so new titles are covered automatically.
- **A seeded, fully editable game list.** Ships with popular anti-cheat titles (Valorant, League, CS2, Dota 2, Fortnite, Apex, Rust, Siege, PUBG, Destiny 2, Tarkov, Call of Duty, Overwatch 2, and more), each marked Default and removable, exactly like the App rules default blocks. Add your own by browsing for the exe or pointing at a running game window; restore the seeds any time.
- **Multi-monitor aware by design.** The decision anchors to the window under your cursor, not to "a game is running somewhere." Game on monitor 1, browser on monitor 2: the browser keeps buttery smooth scrolling while the game stays untouched.
- **Honest and external.** GlideScroll never touches a game's process, memory, or modules. Detection uses only window geometry, process names, service status, and files on disk, the same information Task Manager can see.
- The tray icon tooltip shows why GlideScroll is paused (for example "paused in game (Valorant)") while Game Mode is holding.

### Panning tab, tightened up

- **The Pan engine selector is now the same dropdown as the Scrolling tab's Smoothing engine**, with the description built into the control. Feather and Cushion each get an icon, a title, and a one-line feel description, and the section header spacing matches the Scrolling tab exactly.
- **Fixed: selecting Cushion could squash the pan engine selector out of view**, and no amount of scrolling brought it back. Root cause was a layout rule that let controls compress instead of scroll; fixed for every tab, not just Panning.

### A completely new, fully original theme system

- **All fourteen built-in palettes are 100% originally composed for GlideScroll**, replacing the earlier preset lineup. Every palette passed scripted contrast checks (WCAG) for readable text on every surface.
- **The new lineup:** Auto, Light, Dark, OLED Electric, Cozy Pink, Cozy Blue, Sakura Pop, Lavender Milk, Grid Graphite, Aizome Indigo, Neon Dusk, Cyan Circuit, Green Phosphor, Amber CRT, and Abyss Glow. Auto follows your Windows light/dark setting, switching between Light and Dark.
- **Every theme is editable, not just Custom.** Preferences always shows the selected theme's six colors; change any of them and it saves automatically. The new Reset button beside the Appearance dropdown restores factory colors for just the selected theme, leaving your edits to other themes alone.
- **Three Custom slots** (Custom 1, 2, 3) replace the single Custom palette. Your existing Custom palette carries over into Custom 1.
- **Button text now picks black or white automatically** based on the accent color's brightness, so bright pastel and neon accents stay readable.
- Upgrading keeps your setup: removed themes map to their closest new equivalent (Rose to Cozy Pink, Graphite to Grid Graphite, Parchment to Sakura Pop, Black Ice to Abyss Glow, OLED Black to OLED Electric), and every setting you changed stays changed.

### Scrollbars that match your theme

- **Every scrollbar in the settings window now follows the active theme.** Track, thumb, and hover are drawn from the theme's own colors, with an accent highlight on hover. No more stock gray Windows scrollbars clashing with your look.
- **The old-fashioned arrow stepper buttons are gone entirely.** Nobody clicks scrollbar arrows in 2026; the bar alone is cleaner.

### Scrolling engines, reordered and a new default

- **Silk is now the default engine for new installs.** It is the best all-rounder: as smooth as Liquid Glide, but it settles fast with no lag tail. Existing users keep whatever engine they already picked.
- **The engine dropdown is reordered from most smoothed to least smoothed feel:** Liquid Glide, Silk, Cadence, Smoothed Notch, Notch with Glide, Low Latency.

## v1.2

### Window-class blocks are now yours to control

- **The two surfaces GlideScroll passes through instead of smoothing, Windows File Explorer and the legacy Windows Photo Viewer, are no longer hardcoded.** They now ship as ordinary, removable entries in the App rules exclusion list, each marked with a **Default** badge. If you would rather GlideScroll try to smooth one of them, just delete it from the list. Nothing about the default behavior changes unless you change it, and the one-time upgrade carries the two blocks over automatically.
  - These are matched by **window class** rather than by exe, which is why they can pinpoint File Explorer and the Photo Viewer exactly (the Photo Viewer has no dedicated exe of its own).

### Add class (advanced)

- **A new "Add class (advanced)" option in App rules lets you block or allow a surface by its window class.** This is for the rare surfaces that only accept whole-notch wheel input, where smoothing can't help or gets in the way.
  - **Point-and-capture finder tool.** Press the crosshair and drag it onto any window on screen; GlideScroll reads the exact class under your cursor, so you never have to know or type a class name. You can also type one by hand.
  - **Shared-class safety.** Some window classes are shared across many apps (for example, all Chromium-based apps report the same class). If you target one of those, GlideScroll warns you before you add it, but still lets you if that is what you want.
  - Class blocks affect wheel smoothing only, so middle-button panning keeps working on those surfaces.

### Restore defaults

- **A new "Restore defaults" button with a clear choice.** Pick **Re-add only** to bring back the default blocks while keeping every entry you added, or **Reset everything** to return the list to its factory state. There is also a Cancel, and the confirm button stays disabled until you have made a choice.

### Point at a window

- **Add apps by pointing at them.** The Add running app picker gains a "Point at a window" tool. Drag it onto any window on screen and GlideScroll adds that app, no scrolling a list. It adds the app by its exe, and automatically uses a window-class rule for the couple of surfaces that need one (File Explorer, the Photo Viewer). A small label follows your cursor showing exactly what will be added before you commit.
- **The tray menu is faster too.** Right-clicking the tray icon and choosing "Disable for app" (or "Enable for app" in include mode) now lets you click the window you mean, instead of hunting through a process list. Point, click, done. Esc or right-click cancels.

### Two new smoothing engines

- **Silk**, an online minimum-jerk engine. Each notch plans a smooth trajectory that reaches its target and stops in a fixed, short, bounded time, instead of a spring's long asymptotic creep. As buttery as Liquid Glide, but it settles fast with no lag tail. "Glide duration" sets the settle time.
- **Cadence**, adapts to how you scroll. Isolated or slow notches settle crisply and quickly (reading); fast flick-bursts build momentum and coast (long-page travel). One mode that just does the right thing.
- Both use a new reversal rule: while there is real motion, the first opposite notch is a smooth emergency stop (it brakes to rest and is consumed), and only a second notch launches the new direction from the stopped position. Precise "stop" as a one-flick gesture.

### A featherweight pan engine

- **Feather, a new middle-button pan engine, is now the default.** It drives the page straight from your hand motion, one to one, so panning feels like a sheet of paper under your palm: immediate, light, and free of the slight drag the old engine had. Content tracks your hand with a single frame of latency and nothing more, because it no longer averages your motion over a window or runs it through a filter first.
- **Your previous pan feel is still here, now called Cushion.** It is the softer, more damped engine with the Smoothness filter that absorbs hand tremor. Pick it any time from the new "Pan engine" selector at the top of the Panning tab.
- Both engines flick and coast identically on release, so momentum is unchanged. Feather hides the Smoothness control, since it drives motion directly and has nothing to filter.

### Branding

- GlideScroll is now published by **PortGhost**, and the app moves to **v1.2**.

## v1.1

### Scroll compatibility (the headline fix)

- **App-aware scroll pass-through.** A small number of Windows surfaces only accept whole-notch mouse-wheel input and silently discard the fine-grained events that create smooth motion. On those, earlier versions added the glide delay with no smoothing payoff, so scrolling them felt slower than with GlideScroll turned off. GlideScroll now recognizes these surfaces by window class and lets their scroll input pass straight through, so they scroll instantly at native speed. Smooth scrolling stays fully active in every other application.
  - Confirmed surfaces handled this way: **Windows 11 File Explorer** and the **legacy Windows Photo Viewer**. As a side effect, Photo Viewer wheel zoom works correctly again.
  - Detection runs in the input hook and is cached, so there is no measurable per-event cost and no effect on apps that already worked.

### Smaller download

- **Self-contained build reduced from roughly 72 MB to roughly 46 MB (about 36% smaller) with zero feature loss.**
  - Single-file compression enabled.
  - The unused WPF runtime that the embedded-browser package pulls in is stripped from the bundle.
  - Invariant globalization plus English-only framework resources. The app UI is unaffected; only Windows-supplied dialog text on non-English systems falls back to English.

### Offline operation and a faster UI

- **The settings window now works fully offline.** All icons are embedded in the application instead of being fetched from a content delivery network, so the settings UI renders with no internet connection and no external request.
- **The settings window opens faster.** The embedded browser component is warmed up at application start instead of on first open.
- **Snappier settings UI.** The app list and the running-app picker rebuild and filter more efficiently, removing the hitch when typing into the picker.

### Performance and reliability

- **Lower system-wide input latency and reduced idle CPU.** The global mouse-hook and synthetic-input code paths were rewritten to be allocation-free, eliminating garbage-collector pressure on every mouse event. Foreground-application detection no longer performs an expensive process lookup on each event.
- **Config writes are coalesced.** Holding a plus or minus spinner no longer writes the configuration file to disk on every step; the save happens once, shortly after you stop adjusting.
- **Fixed a settings-versus-scrolling race.** Changing scroll or pan values while actively scrolling could briefly apply a partially-updated set of tunables for a tick or two. Configuration updates are now thread-safe.

---

## v1.0

_Initial public baseline (internal build "v4"). Feature set below._

## Wheel smoothing, multi-engine architecture

- **Four interchangeable smoothing engines** selectable from a dropdown in the Scrolling tab. Each is our own implementation of a distinct smoothing approach.
  - **Damped Spring** (default), 2nd-order spring with tunable damping ratio. PulseScale 1 = bouncy/underdamped, 10 = critical (no overshoot, fastest clean settle), 20 = overdamped/sluggish.
  - **Hybrid: Spring + Coast**, spring chase while actively scrolling, kinetic flywheel coast on release. PulseScale controls the spring/coast time split (1 = mostly coast, 10 = balanced 50/50, 20 = mostly spring).
  - **Momentum Flywheel**, pure inertia with exponential friction decay. Smoothness slider hidden, engine is inherently smooth via continuous physics.
  - **Low Latency**, short linear lerp per notch (capped at 80ms). Smoothness, Acceleration window, and Acceleration boost hidden, by design this mode is snappy not smooth.
- **Direction-reversal clamp** on all physics engines, scrolling down then immediately up zeros prior-direction motion so reversal feels instant.
- **Calibrated impulse** for Flywheel, total integrated scroll distance per notch always equals StepSize regardless of glide duration (mathematically derived: `v0 = StepSize × k`).

## Pan, windowed-sampling rebuild

- **New velocity model.** Pan controller now maintains a sliding window of recent cursor displacement samples and computes velocity as the slope across the window (default 100ms). Replaces the previous accumulation model.
- **Crisp stop response.** When you stop your hand, scroll stops, no ghost momentum bleeding off.
- **Faithful flick.** Exit velocity on release reflects your actual recent hand motion (windowed average), not whatever happened to be in the velocity register at the moment of release.
- **Stale-sample reset.** Long pauses (> 500ms) trigger a buffer clear so a fresh pan session starts with no historical bias.

## Optimize for monitor

- **Per-engine tuning.** The Optimize button now writes mode-appropriate values rather than a single one-size-fits-all formula.
  - Damped Spring → 250ms settle
  - Momentum Flywheel → 400ms coast
  - Hybrid → 320ms split budget
  - Low Latency → 60ms lerp
- **PulseScale = 10** universally (the per-mode "ideal" point: critical for Spring, balanced for Hybrid, no-op for the rest).
- **PPI-aware step size.** Reads native resolution + user-supplied physical screen diagonal (default 27") to scale scroll distance per notch so physical scroll feel is consistent across monitors.
- **Result text per mode.** Status line below the Optimize button reports which engine was tuned and to what values.

## Themes

- **Five preset palettes plus Auto/Light/Dark/Custom**: Rose (soft pink), Graphite (muted dark blue-grey), Parchment (warm cream), Black Ice (cool dark with cyan accent), OLED Black (pure-black for OLED panels).

## Frameless title bar

- Native chrome removed entirely; title bar drawn inside the WebView2 to fully match the app surface.
- `WS_THICKFRAME` retained in `CreateParams` so Aero Snap, Win+arrow snap, and double-click-to-maximize keep working natively.
- `WM_NCCALCSIZE` override eliminates the 1-2px non-client sliver at the top edge.
- Custom min/maximize/close buttons; resize from sides + bottom + bottom corners (top edges intentionally dropped to keep title bar buttons clickable).

## Tray context menu

- **CustomPopupMenu**, frameless `Form` with GDI+ rounded corners (8px radius), drop shadow via `CS_DROPSHADOW`, dark palette.
- Submenus owned by the root form (no chain dismissal on focus shift); deferred deactivation check prevents transient OS focus shuffles from closing the menu.

## Performance + reliability

- **`timeBeginPeriod(1)`** raises system timer resolution to 1ms during pipeline run, pipeline thread sleeps closer to its target tick without burning CPU on long SpinWaits.
- **Autostart self-healing.** HKCU\Run registry entry now syncs to config on every app launch, fixing the first-launch bug (where the default `AutostartWithWindows = true` was a UI lie because nothing wrote the registry entry until the user toggled the checkbox). Also self-heals after EXE moves.

## v3 carryovers (still in this build)

- Tabler icon tabs ("Scrolling" / "Panning" / "App rules") + custom +/- spinners + pinned tab footer.
- Multi-resolution .ico with white/black variants, swapped at runtime based on `SystemUsesLightTheme`.
- 640×620 default form size, single-instance mutex, autostart via HKCU\Run.
