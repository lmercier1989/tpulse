# Hardware metrics: what Pulse reads, and where it cannot

Pulse uses only documented Windows interfaces that work without administrator rights and without a kernel
driver. Where Windows has no such interface for a sensor, Pulse shows nothing rather than an estimate.
Every source is probed once at start-up; what was found is written to the log
(`%LOCALAPPDATA%\Pulse\logs\pulse.log`).

## Supported

| Metric | Source | Works on | Notes |
|---|---|---|---|
| CPU utilization | PDH `\Processor Information(_Total)\% Processor Utility`, falling back to `\Processor(_Total)\% Processor Time` | Every Windows 10/11 system | The same figure Task Manager shows. Utility is measured against the base clock, so it can exceed 100 % under boost; it is capped at 100 %. |
| Per-core CPU | PDH `\Processor Information(*)\% Processor Utility` | Every system | One trace per logical processor (hyper-threads and E-cores included). Toggle with `K` or `-cores`. |
| CPU temperature | PDH `\Thermal Zone Information(*)\High Precision Temperature` (ACPI thermal zones) | Systems whose firmware publishes ACPI thermal zones | Shown as "ACPI zone". A zone whose name contains "CPU" is preferred, otherwise the hottest zone. On many desktops this is a board or chipset sensor, or a constant; see *Not supported*. |
| GPU utilization | PDH `\GPU Engine(*)\Utilization Percentage` | WDDM 2.x drivers, any vendor | Processes are summed per engine and the busiest engine is the GPU figure, exactly like Task Manager. |
| Dedicated GPU memory | PDH `\GPU Adapter Memory(*)\Dedicated Usage`, total from DXGI | WDDM 2.x drivers | Drawn as the second trace (VRAM %) of the GPU graph. |
| GPU temperature | D3DKMT adapter performance data (`KMTQAITYPE_ADAPTERPERFDATA`), NVML as fallback | Dedicated GPUs with WDDM 2.4+ drivers | The source Task Manager uses. Integrated GPUs usually report none. |
| GPU power | NVML `nvmlDeviceGetPowerUsage` (`nvml.dll`, installed by the NVIDIA driver) | NVIDIA GeForce, Quadro, RTX | Board power in watts. Loaded dynamically; absent NVML simply hides the value. |
| Memory | `GlobalMemoryStatusEx` | Every system | Used = total minus available. Standby cache counts as available, as in Task Manager. |
| Disk throughput | PDH `\PhysicalDisk(_Total)\Disk Read Bytes/sec` and `Disk Write Bytes/sec` | Every system | All physical disks together. |
| Disk activity | PDH `\PhysicalDisk(*)\% Idle Time` | Every system | Shown as the busiest disk's active time (100 minus idle). |
| Claude load | `NtQuerySystemInformation` process snapshot | Every 64-bit system | CPU and private memory of the Claude desktop app, its Code session engines and every process below them. Works for the command-line Claude Code too. |
| Claude tokens | Claude Code transcripts in `~/.claude/projects` | Wherever Claude Code runs | Output and fresh input per minute, cache reads, active sessions, context size. Only usage numbers are read. |
| Gateway ping | ICMP echo to the default gateway every 5 s | Networks that answer ping | Shown as "no reply" when the gateway blocks ICMP. |
| A process (`-process`) | `NtQuerySystemInformation` process snapshot | Every 64-bit system | Every process of that image name (or one id) added up: CPU in % of the machine, private working set, threads, handles. |
| A counter (`-counter`) | PDH, the English path as given | Every system | Any counter `typeperf -q` lists. A `*` instance adds up all instances except `_Total`. |
| Git | The reflogs of HEAD (`.git/logs/HEAD`, and per worktree), the state files in `.git`, and `git status` and `git diff --numstat` | Any repository; the changes need git on the PATH | Repositories are found two folders deep on every fixed drive, three deep in the user folder, and in the current folder of every process of the user (read from its process parameters, like Process Explorer), or named with `-git <folder>`. Events come from the reflog without running git. git runs for active repositories every 5 s and for ones with changes every minute, two at a time, with `--no-optional-locks`. Untracked files count as changed files, not as lines. |
| Windows errors | `EventLogWatcher` on the System and Application logs, levels Critical and Error | Every system | The same error from the same source within 5 minutes is marked once. Not read from another machine. |
| Busiest processes | The process snapshot (CPU time, read and write transfer counts per process) | Every 64-bit system | Grouped by name like Task Manager. I/O counts files, pipes and network together; Task Manager's disk column needs a kernel trace, which needs administrator rights. On another machine: `\Process(*)\% Processor Time` and `IO Data Bytes/sec`. |
| Network throughput | IP Helper `GetIfTable2` | Every system | Only physical interfaces that are up: Ethernet, Wi-Fi, mobile broadband. Hyper-V, WSL and Docker switches, VPN tunnels, loopback and filter drivers are left out because their traffic also crosses a physical adapter and would be counted twice. |

All counters are added by their English names (`PdhAddEnglishCounter`), so Pulse works on localized Windows
(for example Dutch or German) where the visible counter names are translated.

## Another machine (`-host`)

The same performance counters are read from the other machine, through its Remote Registry service, as Performance
Monitor does. Where Pulse uses a local API on this machine, it reads a counter there instead:

| Metric | Counter on the other machine |
|---|---|
| CPU, per core, temperature, disk, processes, threads | The same counters as on this machine |
| Memory | `\NUMA Node Memory(_Total)\Total MBytes` and `Available MBytes` |
| Network | `\Network Interface(*)\Bytes Received/sec` and `Bytes Sent/sec`; virtual switches, team interfaces, tunnels and loopback are left out by name |
| Disk space | `\LogicalDisk(*)\% Free Space` and `Free Megabytes`, per drive letter |
| Uptime | `\System\System Up Time` |
| A process | `\Process(*)\% Processor Time`, `Working Set - Private`, `Thread Count`, `Handle Count` |
| CPU name and Windows version | The registry of that machine; the CPU name often needs administrator rights there |

The ping in the panel goes to that machine instead of the gateway. Not read remotely: the GPU, Claude, the battery,
the gateway and DNS, volume labels, and processes by id (name them instead).

It needs the Remote Registry service running there (off by default on Windows 10 and 11, on for Windows Server) and
an account in its *Performance Monitor Users* group, or an administrator there. Pulse checks this before it opens and
says what is missing.

## Not supported

| Metric | Why |
|---|---|
| CPU package temperature, per-core temperature | Needs model-specific registers, which can only be read by a kernel driver. Tools that show it install one (for example WinRing0, now blocked as a vulnerable driver). Pulse does not install drivers. |
| CPU package power | Same reason (RAPL registers). |
| GPU power on AMD and Intel | Their power telemetry is only exposed through vendor SDKs (AMD ADLX, Intel IGCL), which are C++ libraries without a stable C entry point. |
| GPU temperature on most integrated GPUs | Their drivers do not report it through D3DKMT. |
| Fan speeds | No vendor-neutral interface; D3DKMT reports a GPU fan RPM on some boards, which Pulse does not show yet. |
| Several GPUs at once | Pulse shows one GPU: the adapter with the most dedicated memory, which is the discrete GPU on laptops with switchable graphics. Every adapter found is listed in the log. |
| Per-disk and per-adapter graphs | Pulse is an overview instrument; the busiest disk is named by its activity only. |

## Linux

The Linux build reads the kernel's own files, without root and without extra packages. What was found is written to
`~/.local/share/Pulse/logs/pulse.log`.

| Metric | Source | Notes |
|---|---|---|
| CPU utilization, per core | `/proc/stat` | Busy is everything but idle and iowait, per interval, like `top`. |
| CPU temperature | hwmon: `k10temp` or `zenpower` (Tdie, else Tctl), `coretemp` (Package id 0), `cpu_thermal` on ARM boards; else the thermal zones `x86_pkg_temp` or `acpitz` | Shown as "temp". The die sensor, unlike the ACPI zone on Windows. |
| GPU utilization, memory, temperature, power | AMD (and other DRM drivers with `gpu_busy_percent`): sysfs, edge temperature and `power1_average` from the card's hwmon. NVIDIA: NVML (`libnvidia-ml.so.1`) | The card with the most VRAM is shown. The name comes from the system's `pci.ids`. Intel graphics report no load in sysfs and are not shown. |
| Memory | `/proc/meminfo` | Used = MemTotal minus MemAvailable. |
| Disk throughput and activity | `/proc/diskstats` | Whole disks with hardware behind them (`/sys/block/*/device`): partitions, loop, zram, device-mapper and RAID are left out. Activity is the busiest disk's io_ticks. |
| Network throughput | `/proc/net/dev` | Interfaces with hardware behind them (`/sys/class/net/*/device`) that are up; bridges, containers, VPN tunnels and loopback are left out. |
| Processes (Claude, `-process`, busiest, growing memory) | `/proc/[pid]/stat`, `statm`, `io` | Private memory is resident minus shared (anonymous) memory. I/O is `rchar + wchar`, readable for your own processes only. The Claude app is `claude-desktop`, a Code session `claude`. |
| Git process folders | `/proc/[pid]/cwd` | Repositories are found three folders deep in your home folder and two deep on volumes under `/mnt`, `/media`, `/run/media`, `/srv` and `/data`. |
| Errors | `journalctl --follow --priority=3` | What your account may read: the whole system as a member of `wheel`, `adm` or `systemd-journal`, your own messages otherwise. |
| Disk space | `/proc/mounts` | File systems on a block device, each once (btrfs subvolumes and bind mounts under their shortest mount point); `/boot` and `/efi` are left out. |
| Battery | `/sys/class/power_supply` | System batteries only; a mouse or headset battery is not one. |
| Notifications, clipboard | `notify-send`; `wl-copy`, `xclip` or `xsel`, else the terminal (OSC 52) | |

Not on Linux: another machine (`-host`) and performance counters (`-counter`).

## Units

* Throughput uses decimal prefixes: 1 MB/s = 1,000,000 bytes per second, 1 Mbps = 1,000,000 bits per second.
  Disk is shown in bytes per second, network in bits per second, as in Task Manager.
* Memory sizes use binary prefixes like Windows: 1 GB = 1024^3 bytes.
* Temperatures are in degrees Celsius, power in watts.
* Decimal separators follow your Windows region settings.
