using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

using Pulse.Core.Metrics;
using Pulse.Core.Presentation;
using Pulse.Core.Processing;
using Pulse.Core.Settings;
using Pulse.Core.Terminal;

namespace Pulse.Terminal;

/// <summary>
/// The right-click menu of the terminal and what it offers for the thing under the mouse (a moment, a selection, a
/// graph, a row of the side panel), the layout screen (<c>L</c>) that orders and shows the graphs and the sections of the
/// side panel, and the actions both share.
/// </summary>
internal sealed partial class TerminalApp
{
	/// <summary>The row under the mouse in an open menu.</summary>
	private static readonly Rgb MenuSelection = Rgb.FromHex(0x252B38);

	/// <summary>The titles of the panel sections as shown now, for moving and hiding them from the menu.</summary>
	private List<string> _panelTitles = [];

	/// <summary>One row of the menu: its text, the key that does the same, and what it does; no action is a divider.</summary>
	private sealed record MenuItem(string Text, string Key = "", Action? Run = null)
	{
		public static MenuItem Divider { get; } = new("─");

		public bool IsDivider => Run is null;
	}

	// ---- The menu ------------------------------------------------------------------------------------

	/// <summary>Opens the menu of what is at (<paramref name="column"/>, <paramref name="row"/>), just below and right of it.</summary>
	private void OpenMenu(int column, int row)
	{
		var items = MenuFor(column, row);
		CloseMenu();
		var width = Math.Clamp(items.Max(item => item.Text.Length + item.Key.Length) + 7, 26, Math.Max(26, _columns - 2));
		var rows = Math.Min(items.Count + 2, Math.Max(3, _layout!.FooterRow));
		_menu = items;
		_menuSelected = items.FindIndex(item => !item.IsDivider);
		_menuArea = (Math.Clamp(row + 1, 0, Math.Max(0, _layout.FooterRow - rows)), Math.Clamp(column + 1, 0, Math.Max(0, _columns - width)), rows, width);
		_menuKey = string.Empty;
		ClearFlyout();
		RedrawWhilePaused();
	}

	/// <summary>The menu key or Shift + F10: the menu at the mouse, or in the middle of the first graph.</summary>
	private void OpenMenuAtCursor()
	{
		if (_layout is null)
		{
			return;
		}

		if (_mouseCell.Column >= 0)
		{
			OpenMenu(_mouseCell.Column, _mouseCell.Row);
		}
		else if (_layout.Slots.FirstOrDefault(slot => slot.PlotRows > 0) is { PlotRows: > 0 } slot)
		{
			OpenMenu(_layout.Left + (_layout.Width / 2), slot.PlotRow + (slot.PlotRows / 2));
		}
	}

	private void CloseMenu()
	{
		if (_menuArea is { } area)
		{
			InvalidateUnder(area);
		}

		_menu = null;
		_menuArea = null;
		_menuKey = string.Empty;
		RedrawWhilePaused();
	}

	/// <summary>Draws the menu over everything else, again whenever something under it was drawn.</summary>
	private bool DrawMenu(TerminalLayout layout, bool redrawn)
	{
		if (_menu is null || _menuArea is not { } area)
		{
			return false;
		}

		var key = _menuSelected.ToString(CultureInfo.InvariantCulture);
		if (!redrawn && key == _menuKey)
		{
			return false;
		}

		_menuKey = key;
		for (var i = 0; i < area.Rows; i++)
		{
			var row = area.Row + i;
			// Blanked, not erased, so the graph images under the menu stay (Konsole deletes an image a cell of which is erased).
			_frame.MoveTo(row, area.Column).Background(FlyoutBackground).Blank(area.Width);
			var index = i - 1;
			if (index < 0 || index >= _menu.Count)
			{
				continue;
			}

			var item = _menu[index];
			if (item.IsDivider)
			{
				_frame.MoveTo(row, area.Column + 2).Foreground(ScopeColors.Over(ScopeColors.White, 0.14)).Text(new string('─', Math.Max(0, area.Width - 4)));
				continue;
			}

			var selected = index == _menuSelected;
			_frame.MoveTo(row, area.Column + 1).Background(selected ? MenuSelection : FlyoutBackground).Blank(area.Width - 2).MoveTo(row, area.Column + 1)
				.Foreground(selected ? ScopeColors.TextPrimary : ScopeColors.TextSecondary)
				.Text(" " + HeaderLine.Fit(item.Text, area.Width - 6 - item.Key.Length));
			if (item.Key.Length > 0)
			{
				_frame.MoveTo(row, area.Column + area.Width - 2 - item.Key.Length).Foreground(ScopeColors.TextDim).Text(item.Key);
			}

			_frame.Background(FlyoutBackground);
		}

		return true;
	}

	/// <summary>The mouse while the menu is open: pointing picks a row, a click runs it, a click elsewhere closes the menu.</summary>
	private void HandleMenuMouse(MouseInput mouse, bool pressed)
	{
		var (Row, Column, Rows, Width) = _menuArea!.Value;
		var index = mouse.Row - Row - 1;
		var inside = mouse.Column >= Column && mouse.Column < Column + Width && index >= 0 && index < _menu!.Count;
		if (inside && !_menu![index].IsDivider && index != _menuSelected)
		{
			_menuSelected = index;
			RedrawWhilePaused();
		}

		if (!pressed)
		{
			return;
		}

		if (inside && !_menu![index].IsDivider)
		{
			Run(_menu[index]);
		}
		else
		{
			CloseMenu();
		}
	}

	/// <summary>The keys while the menu is open: ↑ ↓ pick, Enter runs, Esc (or any other key) closes.</summary>
	private void HandleMenuKey(KeyPress key)
	{
		switch (key.VirtualKey)
		{
			case UpKey or DownKey:
				var step = key.VirtualKey == UpKey ? -1 : 1;
				for (var index = _menuSelected + step; index >= 0 && index < _menu!.Count; index += step)
				{
					if (!_menu[index].IsDivider)
					{
						_menuSelected = index;
						break;
					}
				}

				RedrawWhilePaused();
				break;
			case EnterKey when _menuSelected >= 0:
				Run(_menu![_menuSelected]);
				break;
			default:
				CloseMenu();
				break;
		}
	}

	private void Run(MenuItem item)
	{
		CloseMenu();
		item.Run?.Invoke();
	}

	/// <summary>
	/// What the menu offers at (<paramref name="column"/>, <paramref name="row"/>): on a plot the moment (or the
	/// selection it lies in) and the graph, on a title the graph, on the side panel what the row stands for and its
	/// section; and always the layout screen and the overview.
	/// </summary>
	private List<MenuItem> MenuFor(int column, int row)
	{
		var layout = _layout!;
		var items = new List<MenuItem>();
		var hit = layout.HitTest(column, row);
		if (hit.Slot >= 0 && hit.Part is LayoutPart.Plot or LayoutPart.Header)
		{
			if (hit.Part == LayoutPart.Plot)
			{
				var time = TimeAtFraction(hit.Fraction);
				items.AddRange(HasSelection && time >= _selectFrom && time <= _selectTo ? SelectionItems() : MomentItems(time, column, row));
				items.Add(MenuItem.Divider);
			}

			items.AddRange(GraphItems(_shown[layout.Slots[hit.Slot].Index]));
		}
		else if (PanelLineAt(column, row) is { } found)
		{
			items.AddRange(PanelItems(found.Line, found.Section));
		}

		if (items.Count > 0)
		{
			items.Add(MenuItem.Divider);
		}

		items.Add(new("Layout of graphs and panel", "L", OpenLayout));
		items.Add(new("Keys and mouse", "?", () =>
		{
			_help = true;
			_helpPage = 0;
			_layoutDirty = true;
		}));
		return items;
	}

	/// <summary>A moment of a plot: freeze it, mark it, copy it, zoom in around it, select the minute before it.</summary>
	private IEnumerable<MenuItem> MomentItems(double time, int column, int row)
	{
		var at = WallAt(time).LocalDateTime.ToString("T", CultureInfo.CurrentCulture);
		yield return new($"Freeze {at}", "Click", () =>
		{
			_pinTime = time;
			_pinCell = (column, row);
			_holdStart = double.NaN;
			_panelDirty = true;
			_headersDirty = true;
			RedrawWhilePaused();
		});
		if (_recording is null)
		{
			yield return new($"Mark {at}", string.Empty, () => MarkMoment(time));
		}

		yield return new("Copy this moment", string.Empty, () => CopyMoment(time));
		yield return new("Zoom in around here", string.Empty, () => ZoomAround(time));
		yield return new("Select the minute before", "Drag", () => SelectRange(time - 60, time));
		if (_zooms.Count > 0)
		{
			yield return new("Zoom out", "Backspace", ZoomOut);
		}

		if (_baseline is not null)
		{
			yield return new("Clear the baseline", string.Empty, () =>
			{
				_baseline = null;
				Toast(Messages.BaselineCleared);
				_panelDirty = true;
				RedrawWhilePaused();
			});
		}
	}

	/// <summary>The selection: what Enter, E, Y, B and Esc do with it.</summary>
	private IEnumerable<MenuItem> SelectionItems()
	{
		yield return new("Zoom in on the selection", "Enter", ZoomToSelection);
		yield return new("Save it as a recording", "E", ExportSelection);
		yield return new("Copy its summary", "Y", CopySelection);
		yield return new("Make it the baseline", "B", ToggleBaseline);
		yield return new("Clear the selection", "Esc", ClearSelection);
	}

	/// <summary>A graph: hide it, its size, its place, only it, and for the CPU its per-core traces and stacking.</summary>
	private IEnumerable<MenuItem> GraphItems(int index)
	{
		var graph = _graphs[index];
		var title = ChannelText.TitleOf(graph.Channel);
		var key = Graph.KeyOf(index).ToString();
		yield return new($"Hide {title}", key, () =>
		{
			graph.SetVisible(false);
			_layoutDirty = true;
		});
		foreach (var (size, text) in new[] { (ChannelSize.Normal, "Normal size"), (ChannelSize.Tall, "Tall"), (ChannelSize.Collapsed, "Fold to its title") })
		{
			if (graph.Size != size)
			{
				yield return new(text, $"Shift + {key}", () =>
				{
					graph.SetSize(size);
					_layoutDirty = true;
				});
			}
		}

		if (graph.OrderName.Length > 0)
		{
			foreach (var (delta, text) in new[] { (-1, "Move up"), (1, "Move down") })
			{
				if (CanMoveGraph(index, delta, shownOnly: true))
				{
					yield return new(text, string.Empty, () => MoveGraph(index, delta, shownOnly: true));
				}
			}
		}

		if (_shown.Count > 1)
		{
			yield return new($"Only {title}", string.Empty, () => Solo(index));
		}

		if (_soloRestore is not null || _graphs.Any(other => !other.Visible))
		{
			yield return new(_soloRestore is not null ? "Back to the graphs before" : "Show all graphs", string.Empty, ShowAllGraphs);
		}

		if (graph.Kind == MetricKind.Cpu)
		{
			yield return new(_settings.ShowCores ? "Hide the per-core traces" : "A trace per CPU core", "K", ToggleCores);
			yield return new(_settings.StackCpu ? "CPU as one trace" : "Stack the CPU by process", "S", ToggleStack);
		}
	}

	/// <summary>A row of the side panel: what it stands for (a process, a repository, an event, a disk), then its section.</summary>
	private IEnumerable<MenuItem> PanelItems(PanelLine line, string section)
	{
		var any = false;
		switch (line.Link)
		{
			case { Kind: PanelLinkKind.Process } process:
				if (CanFollow(process.Value))
				{
					yield return new($"Follow {process.Value} as a graph", string.Empty, () => FollowProcess(process.Value));
				}

				yield return new("Copy the name", string.Empty, () => Copy(process.Value));
				any = true;
				break;
			case { Kind: PanelLinkKind.Repository } repository:
				yield return new("Open the folder", string.Empty, () => OpenFolder(repository.Value));
				yield return new("Open a terminal there", string.Empty, () => OpenTerminal(repository.Value));
				yield return new("Copy the path", string.Empty, () => Copy(repository.Value));
				any = true;
				break;
			case { Kind: PanelLinkKind.Event } item when double.IsFinite(item.Time):
				yield return new("Go to this moment", string.Empty, () => GoTo(item.Time));
				yield return new("Copy the event", string.Empty, () => Copy(item.Value));
				any = true;
				break;
			case { Kind: PanelLinkKind.Disk } disk:
				yield return new(OperatingSystem.IsWindows() ? "Open in Explorer" : "Open the folder", string.Empty, () => OpenFolder(disk.Value));
				any = true;
				break;
		}

		if (_panelTitles.Contains(section))
		{
			if (any)
			{
				yield return MenuItem.Divider;
			}

			var name = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(section.ToLowerInvariant());
			if (_panelTitles.IndexOf(section) > 0)
			{
				yield return new($"Move {name} up", string.Empty, () => MoveSection(_panelTitles, section, -1));
			}

			if (_panelTitles.IndexOf(section) < _panelTitles.Count - 1)
			{
				yield return new($"Move {name} down", string.Empty, () => MoveSection(_panelTitles, section, 1));
			}

			yield return new($"Hide {name}", string.Empty, () => SetSectionShown(section, false));
		}

		if (_settings.HiddenSections.Count > 0)
		{
			yield return new("Show all sections", string.Empty, () =>
			{
				Change(settings => settings.HiddenSections.Clear());
				PanelChanged();
			});
		}
	}

	/// <summary>The row of the side panel at (<paramref name="column"/>, <paramref name="row"/>), with the title of its section.</summary>
	private (PanelLine Line, string Section)? PanelLineAt(int column, int row)
	{
		var layout = _layout!;
		if (layout.PanelWidth == 0 || row < layout.PanelTop || row >= layout.PanelTop + layout.PanelRows || column < layout.PanelLeft)
		{
			return null;
		}

		var count = TerminalLayout.PanelColumnsFor(layout.PanelWidth);
		var width = count == 1 ? layout.PanelWidth : (layout.PanelWidth - TerminalLayout.PanelGap) / 2;
		var part = (column - layout.PanelLeft) / (width + TerminalLayout.PanelGap);
		if (part >= _lastPanel.Length || column - layout.PanelLeft - (part * (width + TerminalLayout.PanelGap)) >= width)
		{
			return null;
		}

		var rows = _lastPanel[part];
		var at = row - layout.PanelTop;
		if (at >= rows.Length || rows[at] is not { } found || found.Line.Kind == PanelLineKind.Blank)
		{
			return null;
		}

		for (var up = at; up >= 0; up--)
		{
			if (rows[up] is { Line.Kind: PanelLineKind.Section } title)
			{
				return (found.Line, title.Line.Label);
			}
		}

		return (found.Line, string.Empty);
	}

	// ---- What the menu does --------------------------------------------------------------------------

	/// <summary>Puts <paramref name="text"/> on the clipboard (on Windows the main thread is a single-threaded apartment).</summary>
	private void Copy(string text) => Toast(TextClipboard.TrySetText(text) ? Messages.Copied : Messages.ClipboardBusy);

	/// <summary>The values of every shown graph at <paramref name="time"/>, and the moment as the flyout shows it, as text.</summary>
	private void CopyMoment(double time)
	{
		if (SampleAt(time) is not { } sample)
		{
			return;
		}

		var text = new StringBuilder($"Pulse · {WallAt(sample.Time).LocalDateTime.ToString("T", CultureInfo.CurrentCulture)}").AppendLine().AppendLine();
		foreach (var index in _shown)
		{
			var readout = ChannelText.Describe(_graphs[index].Channel, _info, sample, showStats: false);
			text.Append($"{readout.Title,-12}{Labelled(readout.PrimaryLabel, readout.PrimaryValue)}");
			text.AppendLine(readout.HasSecondary ? $"   {Labelled(readout.SecondaryLabel, readout.SecondaryValue)}" : string.Empty);
		}

		text.AppendLine();
		foreach (var line in PanelText.Render(InfoPanel.BuildFocus(sample, FocusOn(sample)), 60))
		{
			text.AppendLine(line);
		}

		Copy(text.ToString().TrimEnd() + Environment.NewLine);

		static string Labelled(string label, string value) => label.Length > 0 ? $"{label} {value}" : value;
	}

	/// <summary>Puts a mark at <paramref name="time"/> on every running Pulse, this one included.</summary>
	private void MarkMoment(double time)
	{
		var at = WallAt(time);
		try
		{
			MarkLog.Append(MarkLog.DefaultPath(), at, "mark", "Pulse", $"marked at {at.LocalDateTime.ToString("T", CultureInfo.CurrentCulture)}");
			Toast(Messages.Marked(at.LocalDateTime.ToString("T", CultureInfo.CurrentCulture)));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Toast(Messages.SaveFailed(ex.Message), 6);
		}
	}

	/// <summary>A quarter of the history (10 seconds at least) around <paramref name="time"/>; Backspace goes back.</summary>
	private void ZoomAround(double time)
	{
		_zooms.Push((_settings.HistorySeconds, _viewEnd));
		var length = Math.Max(10, _settings.HistorySeconds / 4);
		_settings.HistorySeconds = length;
		var end = time + (length / 2) + _settings.DisplayDelaySeconds;
		_viewEnd = end >= EdgeTime() - 0.05 ? double.NaN : end;
		_stack = null;
		RefreshChannels();
		Toast(Messages.Zoomed(length));
		_panelDirty = true;
		RedrawWhilePaused();
	}

	/// <summary>Selects [<paramref name="from"/>, <paramref name="to"/>], no earlier than the oldest sample.</summary>
	private void SelectRange(double from, double to)
	{
		_selectFrom = _history.Count > 0 ? Math.Max(from, _history[0].Time) : from;
		_selectTo = to;
		_pinTime = double.NaN;
		_panelDirty = true;
		_headersDirty = true;
		RedrawWhilePaused();
	}

	/// <summary>Shows <paramref name="time"/> at three quarters of the width and freezes it there.</summary>
	private void GoTo(double time)
	{
		var edge = EdgeTime();
		var end = Math.Max(time + _settings.DisplayDelaySeconds + (_settings.HistorySeconds / 4), Math.Min(OldestViewTime(), edge));
		_viewEnd = end >= edge - 0.05 ? double.NaN : end;
		_pinTime = time;
		var fraction = Math.Clamp(1 - ((ViewRightTime() - time) / _settings.HistorySeconds), 0, 1);
		var row = _layout!.Slots.Where(slot => slot.PlotRows > 0).Select(slot => (int?) slot.PlotRow).FirstOrDefault() ?? 0;
		_pinCell = (_layout.Left + (int) (fraction * _layout.Width), row);
		AfterViewChange();
	}

	/// <summary>Whether <paramref name="name"/> can become a graph: on this machine, a free key, not followed yet.</summary>
	private bool CanFollow(string name)
		=> _collector is not null && _info.Host is null
		   && _graphs.Count(graph => graph.Channel.Watch is not null) < WatchSpec.MaximumCount
		   && !_graphs.Any(graph => graph.Channel.Watch is { Kind: WatchKind.Process } watch && string.Equals(watch.Target, name, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Follows the processes named <paramref name="name"/> as one more graph, from now on, as <c>-process</c> would have:
	/// it gets the next key and a buffer of its own. The recording, if any, keeps its columns.
	/// </summary>
	private void FollowProcess(string name)
	{
		if (_collector is null || !_collector.TryFollowProcess(name, out var watch, out var watchIndex))
		{
			Toast(Messages.CannotFollow, 4);
			return;
		}

		var index = _graphs.Count;
		_graphs.Add(Graph.ForWatch(watch, watchIndex));
		var count = _graphs.Count;
		Array.Resize(ref _rasters, count);
		_rasters[index] = new ScopeRaster(ScopePalette.Instance);
		Array.Resize(ref _lastPlot, count);
		_lastPlot[index] = [];
		Array.Resize(ref _lastPlotLength, count);
		Array.Resize(ref _lastPixels, count);
		_lastPixels[index] = [];
		Array.Resize(ref _lastPixelCount, count);
		Array.Resize(ref _lastHeader, count);
		_keys = KeyMap.For(Surface.Terminal, [.. _graphs.Skip(MetricKinds.Builtin.Count).Select(graph => ChannelText.TitleOf(graph.Channel))], _recording is not null);
		_hints = KeyMap.Hints(count, _recording is not null);
		_layoutDirty = true;
		Toast(Messages.Following(name, Graph.KeyOf(index)), 4);
	}

	/// <summary>The folder in Explorer, or in the file manager of the desktop through xdg-open on Linux.</summary>
	private void OpenFolder(string path)
	{
		var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open") { UseShellExecute = false };
		start.ArgumentList.Add(path);
		Launch(start);
	}

	/// <summary>
	/// A new Windows Terminal tab in <paramref name="path"/>, or a PowerShell window there without Windows Terminal. On
	/// Linux: the terminal of $TERMINAL, xdg-terminal-exec or the first common terminal on the PATH, started in that folder.
	/// </summary>
	private void OpenTerminal(string path)
	{
		if (!OperatingSystem.IsWindows())
		{
			string[] candidates =
			[
				Environment.GetEnvironmentVariable("TERMINAL") ?? string.Empty, "xdg-terminal-exec", "x-terminal-emulator", "kgx",
				"gnome-terminal", "konsole", "xfce4-terminal", "foot", "alacritty", "kitty", "wezterm", "ghostty", "xterm",
			];
			if (candidates.FirstOrDefault(name => name.Length > 0 && OnPath(name)) is not { } program)
			{
				Toast(Messages.NoTerminalProgram, 4);
				return;
			}

			Launch(new ProcessStartInfo(program) { UseShellExecute = false, WorkingDirectory = path });
			return;
		}

		var terminal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
		if (File.Exists(terminal))
		{
			var start = new ProcessStartInfo(terminal) { UseShellExecute = false };
			foreach (var argument in new[] { "-w", "0", "new-tab", "-d", path })
			{
				start.ArgumentList.Add(argument);
			}

			Launch(start);
			return;
		}

		Launch(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = path });
	}

	/// <summary>Whether <paramref name="program"/> is a path or a name on the PATH.</summary>
	private static bool OnPath(string program)
		=> program.Contains('/')
			? File.Exists(program)
			: (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
				.Any(folder => File.Exists(Path.Combine(folder, program)));

	private void Launch(ProcessStartInfo start)
	{
		try
		{
			using var process = Process.Start(start);
		}
		catch (Win32Exception ex)
		{
			Toast(ex.Message, 4);
		}
	}

	private void ToggleCores()
	{
		Change(settings => settings.ShowCores = !settings.ShowCores);
		Toast(Messages.Switch("Per-core traces", _settings.ShowCores));
		RedrawWhilePaused();
	}

	private void ToggleStack()
	{
		Change(settings => settings.StackCpu = !settings.StackCpu);
		_stack = null;
		_panelDirty = true;
		_headersDirty = true;
		Toast(Messages.Stacked(_settings.StackCpu));
		RedrawWhilePaused();
	}

	// ---- Order and visibility ------------------------------------------------------------------------

	/// <summary>The built-in graphs in their order now, by name.</summary>
	private List<string> GraphOrderNow()
		=> [.. Graph.DisplayOrder(_graphs, _settings.GraphOrder).Where(index => _graphs[index].OrderName.Length > 0).Select(index => _graphs[index].OrderName)];

	private bool CanMoveGraph(int index, int delta, bool shownOnly)
	{
		var order = GraphOrderNow();
		return !order.SequenceEqual(LayoutOrder.Move(order, name => !shownOnly || _graphs.First(graph => graph.OrderName == name).Visible, _graphs[index].OrderName, delta));
	}

	/// <summary>Moves a built-in graph one place, past the hidden ones when <paramref name="shownOnly"/>; remembered.</summary>
	private void MoveGraph(int index, int delta, bool shownOnly)
	{
		var moved = LayoutOrder.Move(GraphOrderNow(), name => !shownOnly || _graphs.First(graph => graph.OrderName == name).Visible, _graphs[index].OrderName, delta);
		Change(settings => settings.GraphOrder = [.. moved]);
		_layoutDirty = true;
	}

	/// <summary>Only the graph at <paramref name="index"/>; "Back to the graphs before" brings the others back.</summary>
	private void Solo(int index)
	{
		_soloRestore ??= [.. Enumerable.Range(0, _graphs.Count).Where(other => _graphs[other].Visible)];
		for (var other = 0; other < _graphs.Count; other++)
		{
			_graphs[other].SetVisible(other == index);
		}

		_layoutDirty = true;
	}

	private void ShowAllGraphs()
	{
		for (var index = 0; index < _graphs.Count; index++)
		{
			_graphs[index].SetVisible(_soloRestore?.Contains(index) ?? true);
		}

		_soloRestore = null;
		_layoutDirty = true;
	}

	/// <summary>Moves a section of the side panel one place among <paramref name="titles"/>; remembered.</summary>
	private void MoveSection(IReadOnlyList<string> titles, string title, int delta)
	{
		var moved = LayoutOrder.Move(titles, _ => true, title, delta);
		Change(settings => settings.PanelOrder = [.. moved]);
		PanelChanged();
	}

	private void SetSectionShown(string title, bool shown)
	{
		Change(settings =>
		{
			settings.HiddenSections.RemoveAll(hidden => string.Equals(hidden, title, StringComparison.OrdinalIgnoreCase));
			if (!shown)
			{
				settings.HiddenSections.Add(title);
			}
		});
		if (!shown && !_help)
		{
			Toast(Messages.SectionHidden, 4);
		}

		PanelChanged();
	}

	/// <summary>The side panel is laid out anew: every row is written again.</summary>
	private void PanelChanged()
	{
		foreach (var column in _lastPanel)
		{
			Array.Clear(column);
		}

		_panelDirty = true;
	}

	// ---- The layout screen ---------------------------------------------------------------------------

	private void OpenLayout()
	{
		_help = true;
		_helpPage = LayoutPage;
		_layoutCursor = 0;
		_layoutDirty = true;
	}

	/// <summary>Every graph in its order, then every section of the side panel in its order, with whether it is shown.</summary>
	private List<(int Graph, string Name, string Label, bool Shown)> LayoutEntries()
	{
		var entries = new List<(int Graph, string Name, string Label, bool Shown)>();
		foreach (var index in Graph.DisplayOrder(_graphs, _settings.GraphOrder))
		{
			entries.Add((index, _graphs[index].OrderName, $"{Graph.KeyOf(index)}  {ChannelText.TitleOf(_graphs[index].Channel)}", _graphs[index].Visible));
		}

		foreach (var title in SectionTitles())
		{
			entries.Add((-1, title, title, !_settings.HiddenSections.Contains(title, StringComparer.OrdinalIgnoreCase)));
		}

		return entries;
	}

	/// <summary>The sections the side panel has now and the hidden ones, in their order.</summary>
	private List<string> SectionTitles()
	{
		var sample = ViewSample();
		var titles = InfoPanel.Sections(InfoPanel.Build(sample?.Details, sample, null, _events.Latest(InfoPanel.MaximumEvents))).Select(section => section.Title).ToList();
		titles.AddRange(_settings.HiddenSections.Where(hidden => !titles.Contains(hidden, StringComparer.OrdinalIgnoreCase)).Select(hidden => hidden.ToUpperInvariant()));
		return LayoutOrder.Sort(titles, title => title, _settings.PanelOrder);
	}

	/// <summary>The layout screen: the graphs left, the sections of the side panel right, the chosen row lit.</summary>
	private void WriteLayout()
	{
		var entries = LayoutEntries();
		_layoutCursor = Math.Clamp(_layoutCursor, 0, Math.Max(0, entries.Count - 1));
		var graphs = entries.Count(entry => entry.Graph >= 0);
		const int Width = 30;
		const int Gap = 8;
		var left = Math.Max(0, (_columns - ((2 * Width) + Gap)) / 2);
		var top = Math.Max(0, (_rows - Math.Max(graphs, entries.Count - graphs) - 7) / 2);
		_frame.MoveTo(top, left).Bold().Foreground(ScopeColors.TextPrimary).Text("LAYOUT").NotBold()
			.Foreground(ScopeColors.TextDim).Text("   ↑ ↓ choose · Shift + ↑ ↓ move · Space show or hide · Esc done");
		_frame.MoveTo(top + 2, left).Bold().Foreground(ScopeColors.TextSecondary).Text("GRAPHS").NotBold()
			.MoveTo(top + 2, left + Width + Gap).Bold().Text("SIDE PANEL").NotBold();
		for (var i = 0; i < entries.Count; i++)
		{
			var (_, _, label, shown) = entries[i];
			var isGraph = i < graphs;
			var row = top + 3 + (isGraph ? i : i - graphs);
			var column = left + (isGraph ? 0 : Width + Gap);
			var selected = i == _layoutCursor;
			_frame.MoveTo(row, column).Background(selected ? MenuSelection : ScopeColors.Background).Ascii("\u001b[").Number(Width).Byte('X')
				.Foreground(shown ? ScopeColors.For(MetricKind.Cpu) : ScopeColors.TextDim).Text(shown ? " ● " : " ○ ")
				.Foreground(selected ? ScopeColors.TextPrimary : shown ? ScopeColors.TextSecondary : ScopeColors.TextDim).Text(HeaderLine.Fit(label, Width - 4))
				.Background(ScopeColors.Background);
		}

		_frame.MoveTo(top + 4 + Math.Max(graphs, entries.Count - graphs), left).Foreground(ScopeColors.TextDim)
			.Text("Pulse remembers the order and what is shown. Right-click a title or a section does the same.");
	}

	/// <summary>The keys of the layout screen.</summary>
	private void HandleLayoutKey(KeyPress key)
	{
		var entries = LayoutEntries();
		if (entries.Count == 0)
		{
			_help = false;
			_layoutDirty = true;
			return;
		}

		var (graph, name, _, shown) = entries[Math.Clamp(_layoutCursor, 0, entries.Count - 1)];
		switch (key.VirtualKey)
		{
			case UpKey or DownKey when key.Shift:
				var delta = key.VirtualKey == UpKey ? -1 : 1;
				if (graph >= 0 && name.Length > 0 && CanMoveGraph(graph, delta, shownOnly: false))
				{
					MoveGraph(graph, delta, shownOnly: false);
					_layoutCursor += delta;
				}
				else if (graph < 0)
				{
					var titles = SectionTitles();
					var at = titles.IndexOf(name);
					if (at + delta >= 0 && at + delta < titles.Count)
					{
						MoveSection(titles, name, delta);
						_layoutCursor += delta;
					}
				}

				break;
			case UpKey:
				_layoutCursor = Math.Max(0, _layoutCursor - 1);
				break;
			case DownKey:
				_layoutCursor = Math.Min(entries.Count - 1, _layoutCursor + 1);
				break;
			case ' ' when graph >= 0:
				_graphs[graph].SetVisible(!shown);
				break;
			case ' ':
				SetSectionShown(name, !shown);
				break;
			case EscapeKey or EnterKey or 'L' or 'Q':
				_help = false;
				break;
		}

		_layoutDirty = true;
	}
}
