class Program
{
    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine();
        Console.WriteLine("  ╔══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("  ║           DEVICE SECTION ASSIGNMENT SIMULATOR                                ║");
        Console.WriteLine("  ║           6 sections  ·  3 slots each  ·  cascading displacement             ║");
        Console.WriteLine("  ╚══════════════════════════════════════════════════════════════════════════════╝");

        var manager = new SectionManager(sectionCount: 6, capacityPerSection: 3);

        // -- Progress : ON INTERNAL TESTING - still in try and error it still not working as per requested
        // ── Phase 1: fill all slots ──────────────────────────────────────── 
        Console.WriteLine();
        Console.WriteLine("  ██  PHASE 1 — INITIAL FILL");

        var initialDevices = new List<Device>
        {
            //                        S1    S2    S3    S4    S5    S6
            MakeDevice("AAA001",      91,   45,   30,   20,   15,   10),
            MakeDevice("BBB002",      20,   88,   40,   25,   18,   12),
            MakeDevice("CCC003",      25,   30,   85,   50,   22,   14),
            MakeDevice("DDD004",      95,   40,   28,   18,   13,    9),
            MakeDevice("EEE005",      18,   82,   35,   28,   20,   11),
            MakeDevice("FFF006",      22,   27,   78,   45,   25,   16),
            MakeDevice("GGG007",      15,   22,   30,   87,   40,   20),
            MakeDevice("HHH008",      85,   38,   24,   16,   11,    8),
            MakeDevice("III009",      14,   78,   33,   26,   19,   10),
            MakeDevice("JJJ010",      19,   24,   72,   42,   28,   18),
            MakeDevice("KKK011",      12,   18,   26,   82,   38,   22),
            MakeDevice("LLL012",      80,   35,   22,   15,   10,    7),
            MakeDevice("MMM013",      11,   72,   30,   24,   17,    9),
            MakeDevice("NNN014",      17,   21,   68,   40,   30,   20),
            MakeDevice("OOO015",      10,   15,   24,   79,   35,   21),
            MakeDevice("PPP016",      13,   19,   22,   30,   88,   42),
            MakeDevice("QQQ017",      16,   23,   28,   35,   84,   38),
            MakeDevice("RRR018",       9,   14,   20,   28,   80,   45),
        };
        
        foreach (var d in initialDevices)
            manager.Add(d);

        // ── Phase 2: incoming devices — triggers cascade ───────────────────
        Console.WriteLine();
        Console.WriteLine("  ██  PHASE 2 — INCOMING DEVICES  (all sections full — cascade begins)");

        var incomingDevices = new List<Device>
        {
            MakeDevice("NEW001",      97,   55,   33,   21,   14,   11),
            MakeDevice("NEW002",      20,   30,   93,   60,   32,   18),
            MakeDevice("NEW003",      12,   18,   35,   89,   44,   70),
        };

        foreach (var d in incomingDevices)
            manager.Add(d);

        // ── Final state ────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("  ██  FINAL STATE");
        Console.WriteLine();
        manager.PrintSectionTable();

        Console.WriteLine();
        Console.WriteLine("  Done. Press any key to exit.");
        Console.ReadKey();
    }

    static Device MakeDevice(string id,
        double s1, double s2, double s3, double s4, double s5, double s6)
    {
        return new Device(id, new Dictionary<int, double>
        {
            { 1, s1 }, { 2, s2 }, { 3, s3 },
            { 4, s4 }, { 5, s5 }, { 6, s6 }
        });
    }
}