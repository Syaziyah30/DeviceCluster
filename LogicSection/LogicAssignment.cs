using System;
using System.Collections.Generic;
using System.Linq;

// ══════════════════════════════════════════════
//  DEVICE MODEL
// ══════════════════════════════════════════════

class Device
{
	public string Id { get; }
	public Dictionary<int, double> Scores { get; }

	public Device(string id, Dictionary<int, double> scores)
	{
		Id = id;
		Scores = scores;
	}
}

// ══════════════════════════════════════════════
//  SECTION MANAGER
// ══════════════════════════════════════════════

class SectionManager
{
	private readonly int _sectionCount;
	private readonly int _capacity;
	private readonly Dictionary<int, List<Device>> _sections;

	// Track devices that truly have no valid home
	private readonly List<Device> _unassigned = new List<Device>();

	public SectionManager(int sectionCount, int capacityPerSection)
	{
		_sectionCount = sectionCount;
		_capacity = capacityPerSection;
		_sections = new Dictionary<int, List<Device>>();
		for (int i = 1; i <= sectionCount; i++)
			_sections[i] = new List<Device>();
	}

	// ── Public entry point ─────────────────────────────────────────────────

	public void Add(Device device)
	{
		Console.OutputEncoding = System.Text.Encoding.UTF8;

		PrintDivider();
		Console.WriteLine($"  ▶  INCOMING DEVICE : {device.Id}");
		PrintScoreTable(device);
		Console.WriteLine();

		var log = new List<string>();
		PlaceDevice(device, new HashSet<int>(), 0, log);

		// Print cascade log
		Console.WriteLine("  PLACEMENT LOG:");
		foreach (var line in log)
			Console.WriteLine(line);

		Console.WriteLine();
		PrintSectionTable();
	}

	// ── Recursive placement with cascade ──────────────────────────────────

	private void PlaceDevice(Device device, HashSet<int> visited, int depth, List<string> log)
	{
		string pad = new string(' ', depth * 4 + 4);

		int target = device.Scores
			.Where(kv => !visited.Contains(kv.Key))
			.OrderByDescending(kv => kv.Value)
			.Select(kv => kv.Key)
			.FirstOrDefault();

		if (target == 0)
		{
			log.Add($"{pad}⚠  {device.Id} has no valid section remaining → UNASSIGNED");
			_unassigned.Add(device);
			return;
		}

		var section = _sections[target];
		double myScore = device.Scores[target];

		log.Add($"{pad}→  Trying S{target}  (my score: {myScore:F1}%,  slots: {section.Count}/{_capacity})");

		if (section.Count < _capacity)
		{
			section.Add(device);
			log.Add($"{pad}✔  {device.Id} placed in S{target}  (free slot)");
			return;
		}

		// Section full — compare against lowest occupant
		Device lowest = section.OrderBy(d => d.Scores[target]).First();
		double lowestScore = lowest.Scores[target];

		if (myScore > lowestScore)
		{
			section.Remove(lowest);
			section.Add(device);
			log.Add($"{pad}✔  {device.Id} ({myScore:F1}%)  DISPLACES  {lowest.Id} ({lowestScore:F1}%)  from S{target}");
			log.Add($"{pad}↪  Cascading: re-homing {lowest.Id}...");

			var nextVisited = new HashSet<int>(visited) { target };
			PlaceDevice(lowest, nextVisited, depth + 1, log);
		}
		else
		{
			log.Add($"{pad}✘  {device.Id} ({myScore:F1}%)  cannot beat lowest  {lowest.Id} ({lowestScore:F1}%)  → try next section");
			visited.Add(target);
			PlaceDevice(device, visited, depth + 1, log);
		}
	}

	// ── Print helpers ──────────────────────────────────────────────────────

	private static void PrintDivider()
	{
		Console.WriteLine();
		Console.WriteLine("  " + new string('═', 78));
	}

	private static void PrintScoreTable(Device d)
	{
		// Header
		Console.Write("  ┌");
		for (int i = 0; i < d.Scores.Count; i++)
			Console.Write("───────────" + (i < d.Scores.Count - 1 ? "┬" : "┐"));
		Console.WriteLine();

		Console.Write("  │");
		foreach (var kv in d.Scores.OrderBy(x => x.Key))
			Console.Write($"  Section {kv.Key}  │");
		Console.WriteLine();

		Console.Write("  ├");
		for (int i = 0; i < d.Scores.Count; i++)
			Console.Write("───────────" + (i < d.Scores.Count - 1 ? "┼" : "┤"));
		Console.WriteLine();

		Console.Write("  │");
		foreach (var kv in d.Scores.OrderBy(x => x.Key))
			Console.Write($"  {kv.Value,6:F1}%   │");
		Console.WriteLine();

		Console.Write("  └");
		for (int i = 0; i < d.Scores.Count; i++)
			Console.Write("───────────" + (i < d.Scores.Count - 1 ? "┴" : "┘"));
		Console.WriteLine();
	}

	public void PrintSectionTable()
	{
		Console.WriteLine("  SECTION STATE:");
		Console.WriteLine("  ┌─────────────┬──────────────────────────────────────────────────────┬────────┐");
		Console.WriteLine("  │ Section     │ Devices (score in this section)                      │ Slots  │");
		Console.WriteLine("  ├─────────────┼──────────────────────────────────────────────────────┼────────┤");

		for (int s = 1; s <= _sectionCount; s++)
		{
			var devices = _sections[s];
			string devList = devices.Count == 0
				? "(empty)"
				: string.Join(", ", devices
					.OrderByDescending(d => d.Scores.ContainsKey(s) ? d.Scores[s] : 0)
					.Select(d => $"{d.Id} {(d.Scores.ContainsKey(s) ? d.Scores[s].ToString("F1") : "?")}%"));

			// Truncate if too long for column
			if (devList.Length > 52) devList = devList.Substring(0, 49) + "...";

			string slots = $"[{devices.Count}/{_capacity}]";
			string slotPad = slots.PadRight(6);
			Console.WriteLine($"  │ Section {s,-4} │ {devList,-52} │ {slotPad} │");
		}

		Console.WriteLine("  ├─────────────┼──────────────────────────────────────────────────────┼────────┤");

		if (_unassigned.Count > 0)
		{
			string uList = string.Join(", ", _unassigned.Select(d => d.Id));
			if (uList.Length > 52) uList = uList.Substring(0, 49) + "...";
			Console.WriteLine($"  │ UNASSIGNED  │ {uList,-52} │ {"[" + _unassigned.Count + "]",-6} │");
		}
		else
		{
			Console.WriteLine($"  │ UNASSIGNED  │ {"(none)",-52} │ {"[0]",-6} │");
		}

		Console.WriteLine("  └─────────────┴──────────────────────────────────────────────────────┴────────┘");
	}
}