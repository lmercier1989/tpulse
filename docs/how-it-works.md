# How Pulse works

The details behind the [README](../README.md): architecture, rendering, cost, files and project layout.
Which metrics come from where, and what Windows cannot provide, is in [hardware-metrics.md](hardware-metrics.md).

## Architecture

```
 PeriodicTimer loop (thread pool)        main thread: terminal loop
 ┌────────────────────────────┐          ┌──────────────────────────────────────────────────┐
 │ SystemMetricsCollector     │ snapshot │ Channel: ring buffers, window min/max, auto scale │
 │  PDH query (one collect)   ├─────────►│ CurvePlanner: monotone cubic per trace            │
 │  GlobalMemoryStatusEx      │  queue   │ ScopeRaster: anti-aliased pixels ─► SixelEncoder  │
 │  GetIfTable2               │          │   or BrailleScope: 2 x 4 dots per cell            │
 │  DXGI, D3DKMT, NVML        │          │ InfoPanel: system, Claude, network, disks         │
 │  DriveInfo, NetworkInfo    │          │ AnsiWriter: one synchronized write per frame      │
 │  process snapshot (Claude, │          │ CsvRecorder: one row per sample (-record)         │
 │   -process)                │          │                                                   │
 │  transcripts (tokens)      │          │                                                   │
 │  -counter paths            │          │                                                   │
 └────────────────────────────┘          └──────────────────────────────────────────────────┘
```

* **Two independent loops.** Sampling runs on a `PeriodicTimer` on the thread pool. The terminal loop reads
  keys, applies samples and draws when a frame is due; in between it sleeps on a high-resolution waitable timer
  that also wakes on a key press, so it never polls.
* **Slow facts, slowly.** Disk space, the network connection, the Windows version and the battery are read every
  5 seconds; process and thread counts come from the `\System` counters with every sample.

## Claude

* **Load.** Every sample, one `NtQuerySystemInformation` call returns all processes with their parent, CPU time and
  private working set (no handle per process). The Claude desktop app and its helpers run as `claude.exe` from
  `WindowsApps\Claude_*` (or `AnthropicClaude`); every Code session runs its own engine, also `claude.exe`, from
  elsewhere (the app's `claude-code` folder, or a command-line install). Everything below an engine, at any depth, is a
  tool of that session: shells, builds, tests, git, MCP servers. A parent only counts when it was created before its
  child, because Windows reuses process ids. CPU is the change in CPU time per sample, in % of the whole machine, like
  Task Manager.
* **Tokens.** Claude Code writes a JSON line per content block of every answer to `~/.claude/projects/*/*.jsonl`
  (`CLAUDE_CONFIG_DIR` is honoured). A file watcher (plus a 15 s rescan as a safety net) reads only what was appended;
  usage is counted once per message id and only its growth is added, so an answer written in three blocks counts once.
  Files that existed when Pulse started are read for their latest state only, so old answers never count as new. Only
  usage, model, time, folder and branch are read; message content is never parsed.
* **Context.** The context size of a session is the input of its latest main-thread call (fresh input, cache writes and
  cache reads); subagent calls do not change it.
* **Recording.** `-record` appends one CSV row per sample with the wall-clock time, all hardware readings, Claude's CPU
  and memory per part, and the tokens written during that sample (they add up over rows).

## Extra graphs and other machines

* **Up to three extra graphs.** `-process` and `-counter` add graphs after the six built-in ones, on the keys 7 to 9.
  They live for one launch: their visibility and size are not remembered, and naming one shows only the graphs named
  on that command line.
* **A process** is found in the same process snapshot the Claude split uses, by image name, so watching it costs no
  extra system call. CPU is measured per process like Task Manager does, and a process that started since the last
  sample counts with all of its time; memory is the private working set.
* **A counter** is added to the one PDH query by its English path. Its unit comes from its name: `%` reads as a share,
  `Bytes/sec` as throughput, `Bytes` as a size, `/sec` as a rate, anything else as a plain number. A share zooms in
  on its scale but stops at 100 % while the data stays within it.
* **Another machine** (`-host`) prefixes every counter path with `\\host`. What only a local API gives (memory,
  network, disk space) comes from counters there, and the panel shows that machine. Before the tab opens, `tpulse`
  connects once (`PdhConnectMachine`) and checks every counter path, so a wrong name or missing rights is reported in
  the shell, not in a tab that closes again.

## Git, events and history

* **Events.** A sample carries the events that happened since the previous one: reflog lines of the followed
  repositories and errors from the Windows logs. Each gets a place on the sample clock from its wall-clock time, so a
  marker sits where it happened, not where it was read. The app keeps the latest 500.
* **Finding repositories.** Without `-git <folder>`, Pulse scans the usual places in the background every 10 minutes
  (two folders deep on every fixed drive, three in the user folder, skipping Windows, program folders, AppData,
  hidden folders and links, at most 6,000 folders) and every 10 seconds reads the current folder of every new process
  of the user; a repository with a process in it counts as in use. A reflog is followed by one repository only, so a
  worktree's events count once.
* **Git.** The reflog is read only where it grew; worktrees are found every 15 seconds. The steps inside a rebase are
  skipped, its start and end are kept. `git status --porcelain=v2 --branch` and `git diff HEAD --numstat` run in the
  background: every 5 seconds for an active repository (HEAD moved or a process worked in it in the last 10 minutes,
  every 30 when git is slow there), every minute for one with changes, at once when HEAD moved, never for one that
  rests; at most two at a time.
* **Markers** are drawn before the traces, as a dashed line in the raster, in braille dots or as a WPF hairline, so a
  trace always stays on top. A spike or a leak is drawn on its own graph only.
* **Marks** go through `%LOCALAPPDATA%\Pulse\marks.log`: `tpulse -mark` and `tpulse -- command` append a line (time,
  action, source, text), and every running Pulse reads what was added since it started, once per sample. Past 256 KB
  the next mark starts the file over.
* **Spikes.** For CPU, GPU, disk and network the level is the median of the last 30 samples. A percentage spikes when
  it rises 30 points above it and reaches 50 %; a rate when it reaches four times the level plus 10 MB/s (or 10 Mbps)
  and at least 50. The cause is the process whose CPU (or I/O) rose most compared to its average over those samples.
  A graph reports again only after it came back down, and at most once in 20 seconds.
* **Growing memory.** Every 10 seconds the private memory of every process of at least 100 MB is noted. A line fitted
  through its last ten minutes that rises at least 200 MB (and a fifth of where it started), with an R² of at least
  0.85, makes the process a suspect: reported once, and again after every further 500 MB.
* **A measured command** runs as a child process in the same console (a program directly, anything else through
  `cmd /c`), while a sampler takes a sample every second; disk and network rates are added up over time. Ctrl+C goes
  to the command, and the summary still follows.
* **Scrolling back** anchors the right edge at a moment on the sample clock while sampling goes on, so no gap opens;
  the channels recompute their window and scale for the moment shown, and the headers take the sample of that moment
  from a ring buffer of samples.
* **A selection** is a range on the sample clock, so it moves with the trace while sampling goes on. Its statistics
  come from the series of every shown graph; the busiest processes from the top ten that every sample carries. A
  zoom sets the history to the selection for this launch only. A baseline keeps a copy of every main trace over its
  range, because the ring buffer moves on; it is drawn by planning that copy with a mapping shifted to where it is
  compared.
* **Stacked CPU.** The five process names with the highest average CPU over the visible window get a band. For every
  pixel column the bands are interpolated between the two samples around it; a name that is not among a sample's top
  ten adds nothing there, and the last band fills up to the total CPU.
* **Recordings** play back by turning every CSV row into a sample again, on a clock that starts at 0, with buffers as
  large as the recording. Columns are found by name, so older recordings still play.
* **Notifications** use a notification-area icon on a thread of its own; Windows 10 and 11 turn its balloon into a
  notification without any app registration.

## Rendering

* **Curves.** Samples are joined with a monotone cubic (Steffen's method, as in d3's `curveMonotoneX`): smooth,
  but it never rises above or dips below the two samples it connects, so it shows no value that was not measured.
  The trace runs two samples behind the numeric readout so a curve segment is final before it scrolls in; a dot
  marks where the trace meets the right edge.
* **Pixels in the terminal.** Each graph is rasterized in software: the trace is sampled once per pixel column
  and every nearby pixel gets a coverage from its distance to the curve, in six anti-aliasing levels, over a
  gradient fill. The image is sent as Sixel with only the drawn pixels encoded (the background is transparent
  over cells erased to the panel colour), and only when it changed, all inside one synchronized update.
* **Braille fallback.** Terminals without Sixel get 2 x 4 braille dots per cell in 24-bit colour, with the
  gradient fill as cell background.
* **Gaps.** A pause, sleep or missing reading breaks the trace instead of drawing a line across it.
* **Dense data.** When samples are closer than two pixels (5 minutes at 0.5 s), each pixel column keeps its first,
  lowest, highest and last sample, which keeps every peak while bounding the work.
* **Scaling.** Percentages use a fixed 0 to 100 % scale. Throughput scales to the next 1-2-5 step above the
  visible peak (with headroom and a floor of 1 MB/s or 1 Mbps), shrinks only when the data fits a smaller step
  comfortably, and eases between steps.
* **Info panel.** About a quarter of the terminal in one column (32 to 50 wide), and from 220 columns on a third of
  it in two columns that are evened out at a section break. Every section gets a label column fitted to its own labels,
  short values are right-aligned, and a section title runs into a thin rule. Every row has a priority; when rows run
  short the blank spacing goes first, then the least important rows. A section title never stands alone.
* **Mouse.** The console reports mouse events (Windows Terminal passes them on); the wheel scrolls through the
  history, a click on a title folds the graph, and over a plot a cursor line marks the moment whose values the
  headers show and whose busiest processes the panel lists. Pressing the button freezes that moment, which then wins
  over the cursor wherever the mouse goes: a press held for 0.4 s or longer lets go with the button, a shorter click
  keeps it until the next click on a plot or `Esc`; moving two cells or more with the button down selects a range
  instead. Shift + drag still selects text. The window draws its tooltip in a popup of its own, because the WPF
  tooltip service closes a tooltip on every mouse down.
* **One vocabulary.** The keys, the mouse gestures and the marker colours live in one key map (`KeyMap`), which the
  terminal's `?`, the window's `F1` and the README follow; a letter does the same on both surfaces or belongs to one.
  Messages after a key come from one place (`Messages`): sentence case, a hint to a key reads "Key verb" ("End live",
  "Esc clear"). A length of time is written one way (`Units.Duration`: "45 s", "1 min 5 s", "1 h 5 min"), a share of a
  process with one decimal, a graph value as a whole percentage. Colours carry meaning: each graph its own, what you set
  (a mark, a selection) sky blue, what Pulse found orange (a spike) or pink (a leak), an error red; the stacked bands use
  colours no marker uses. A section title is a capitalised noun with its detail as written ("SELECTION 14:32:05 ·
  1 min 5 s"); a list of figures gets a dim row naming its columns.
* **Right button.** The console reports both buttons; a press and release without moving opens the menu, a move with
  the button down pans: the view moves by one column of history per cell. The menu is drawn like the flyout, after
  it, and again whenever something under it was drawn; while it is open the mouse and the keys work the menu. It
  offers what fits the target: the layout of the screen tells a plot, a title or the side panel apart, and every row
  of the side panel carries what it stands for (a process, a repository, an event, a disk) and belongs to the section
  above it.
* **Following a process while running.** The collector takes one more process watch between two samples (a lock keeps
  a sample and an addition apart), marks its image name in the process snapshot and reads it from the next sample on;
  the app adds a graph with buffers of its own. A recording keeps the columns it started with.
* **Layout.** The built-in graphs and the sections of the side panel are ordered by a list of names in the settings;
  a name the list does not hold keeps its natural place after the ones it does, so a section that appears later
  still shows. Moving trades places with the nearest shown neighbour. The keys stay with their graph wherever it is.
* **Flyout.** The moment under the mouse is written as text over the graphs, after the plots of a frame, and again
  whenever a plot under it was sent. When it moves or closes, the next frame erases where it was and sends the
  headers and plots there again.

## Cost

Frames follow the scroll speed (about one per pixel moved, at most 30 per second), so a long history costs less.
With all five graphs on a 150 x 46 terminal: about 5 % of one CPU core and about 230 KB/s to the terminal at the
default 60 s history; braille mode about 1 %. History lives in fixed-size ring buffers; PDH arrays are read into
one reusable native buffer and instance names are parsed as spans, so steady-state sampling barely allocates.

## In Windows Terminal

`install.ps1` adds a Pulse profile as a Windows Terminal *fragment* (your `settings.json` is never edited): the
instrument background from edge to edge, no scroll bar, the Pulse icon and Consolas 11 pt. Typed in a shell inside
Windows Terminal, `tpulse` opens a new tab with that profile (`wt -w 0 new-tab -p Pulse`) and returns; `-here`
draws in the current tab instead, and outside Windows Terminal it always draws in place.

## Files

| What | Where |
|---|---|
| Program | `%LOCALAPPDATA%\Programs\Pulse` (`tpulse.exe`, `pulse.ico`) |
| Windows Terminal profile | `%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\Pulse\pulse.json` |
| Start menu shortcut | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Pulse.lnk` |
| Settings | `%LOCALAPPDATA%\Pulse\settings.json` (delete it to reset) |
| Profiles | `%LOCALAPPDATA%\Pulse\profiles.json` (created with the samples on first use) |
| Recordings | `Documents\Pulse\pulse-<time>.csv`, unless `-record` names a file; a saved selection is `selection-pulse-<time>.csv` |
| Marks | `%LOCALAPPDATA%\Pulse\marks.log` (starts over past 256 KB) |
| Log | `%LOCALAPPDATA%\Pulse\logs\pulse.log` (rolls over at 1 MB): the sources found, the renderer the terminal got and what drawing costs |

## Project layout

```
src/Pulse.Core          metric sources (interop), sampling loop, ring buffer, scaling, curve planning,
                        terminal rendering (raster, Sixel, braille, layout, info panel), settings, command line
src/Pulse               tpulse.exe: the terminal loop and console handling, and the WPF window for -window
tests/Pulse.Core.Tests  xUnit tests: buffers, scaling, curves, rates, Sixel round trip, raster, layout, panel,
                        counter paths, process and counter graphs, the remote mode against this machine
docs/                   these notes, hardware support and the screenshots
```
