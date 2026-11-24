using System.Diagnostics;
using System.Globalization;
using LibreHardwareMonitor.Hardware;
using ResetExplode;

// Testes do motor de sensores (etapa 1). Sai com código 1 se algum falhar.
var t = new TestRunner();
string? logPath = args.FirstOrDefault();

t.Run("Tipos de hardware caem no grupo certo", () =>
{
    T.Eq(SensorTier.Fast, SensorSchedule.TierOf(HardwareType.Cpu));
    T.Eq(SensorTier.Fast, SensorSchedule.TierOf(HardwareType.GpuNvidia));
    T.Eq(SensorTier.Fast, SensorSchedule.TierOf(HardwareType.GpuAmd));
    T.Eq(SensorTier.Fast, SensorSchedule.TierOf(HardwareType.GpuIntel));
    T.Eq(SensorTier.Fast, SensorSchedule.TierOf(HardwareType.Memory));
    T.Eq(SensorTier.Medium, SensorSchedule.TierOf(HardwareType.Motherboard));
    T.Eq(SensorTier.Medium, SensorSchedule.TierOf(HardwareType.Network));
    T.Eq(SensorTier.Slow, SensorSchedule.TierOf(HardwareType.Storage));
});

t.Run("Cada grupo é lido no seu ritmo (modo normal)", () =>
{
    // 10x mais rápido: CPU 100 ms, placa-mãe 200 ms, disco 1,5 s. Em 3 s: ~30, ~15, ~2-3 leituras.
    var cpu = Fake.Hw("Ryzen 7 5700X", HardwareType.Cpu);
    var mobo = Fake.Hw("B550M-PX", HardwareType.Motherboard);
    var ssd = Fake.Hw("NVMe", HardwareType.Storage);
    using var hub = new SensorHub(new IHardware[] { cpu, mobo, ssd }, new SensorSchedule { Speedup = 10 });
    hub.Start();
    Thread.Sleep(3000);
    hub.Dispose();
    T.Between(cpu.Updates, 25, 32, "CPU");
    T.Between(mobo.Updates, 12, 17, "placa-mãe");
    T.Between(ssd.Updates, 2, 3, "disco");
});

t.Run("Disco lento não trava a janela (antes travava a cada segundo)", () =>
{
    // SSD que leva 40 ms por leitura de SMART (realista com 3-4 discos). Simula 60 "segundos" da janela.
    var hw = new IHardware[] { Fake.Hw("CPU", HardwareType.Cpu, sensors: 40), Fake.Hw("GPU", HardwareType.GpuNvidia, sensors: 60),
                               Fake.Hw("SSD", HardwareType.Storage, cost: TimeSpan.FromMilliseconds(40), sensors: 15),
                               Fake.Hw("HDD", HardwareType.Storage, cost: TimeSpan.FromMilliseconds(40), sensors: 15) };

    // Como era: a janela chamava Update() de todo o hardware.
    double oldMax = 0;
    for (int i = 0; i < 5; i++)
    {
        var sw = Stopwatch.StartNew();
        foreach (var h in hw) h.Update();
        oldMax = Math.Max(oldMax, sw.Elapsed.TotalMilliseconds);
    }

    // Como ficou: a janela só aplica o snapshot pronto.
    using var hub = new SensorHub(hw, new SensorSchedule { Speedup = 10 });
    var table = new SensorTable();
    hub.Start();
    double newMax = 0;
    for (int i = 0; i < 60; i++)
    {
        var sw = Stopwatch.StartNew();
        table.Apply(hub.Latest);
        newMax = Math.Max(newMax, sw.Elapsed.TotalMilliseconds);
        Thread.Sleep(10);
    }
    hub.Dispose();
    Console.WriteLine($"      pior tempo na thread da janela: antes {oldMax:0.0} ms, agora {newMax:0.00} ms ({table.Rows.Count} sensores)");
    T.True(oldMax >= 80, "o jeito antigo deveria levar ≥ 80 ms");
    T.True(newMax < 5, $"a janela não pode esperar o driver ({newMax:0.00} ms)");
});

t.Run("Custo da coleta por segundo cai (disco a cada 15 s em vez de 1 s)", () =>
{
    var cost = TimeSpan.FromMilliseconds(4);
    var hw = new IHardware[] { Fake.Hw("CPU", HardwareType.Cpu, cost: TimeSpan.FromMilliseconds(1)),
                               Fake.Hw("SSD", HardwareType.Storage, cost: cost), Fake.Hw("HDD", HardwareType.Storage, cost: cost) };
    using var hub = new SensorHub(hw, new SensorSchedule { Speedup = 10 });
    hub.Start();
    Thread.Sleep(3200);
    var st = hub.Latest!.Stats;
    hub.Dispose();
    // Antigo: (1 + 4 + 4) ms a cada "segundo" = 9 ms/s. Novo: CPU 1 ms/s + discos 8 ms a cada 15 s ≈ 1,5 ms/s (escala real).
    double realScale = st.CostMsPerSecond / 10; // Speedup 10: tudo acontece 10x por segundo de relógio
    Console.WriteLine($"      custo medido ≈ {realScale:0.0} ms por segundo real (antes ~9 ms/s); mais lento: {st.SlowestHardware}");
    T.True(realScale < 3.5, $"custo {realScale:0.0} ms/s");
});

t.Run("Economia (minimizado) lê 5x menos", () =>
{
    var cpu = Fake.Hw("CPU", HardwareType.Cpu);
    using var hub = new SensorHub(new IHardware[] { cpu }, new SensorSchedule { Speedup = 10 }) { Mode = SensorMode.Economy };
    hub.Start();
    Thread.Sleep(2000);
    hub.Dispose();
    T.Between(cpu.Updates, 3, 5, "CPU em economia (500 ms em escala)");
});

t.Run("Lista de sensores aberta acelera placa-mãe e discos", () =>
{
    var mobo = Fake.Hw("B550M-PX", HardwareType.Motherboard);
    var ssd = Fake.Hw("NVMe", HardwareType.Storage);
    using var hub = new SensorHub(new IHardware[] { mobo, ssd }, new SensorSchedule { Speedup = 10 }) { DetailWanted = true };
    hub.Start();
    Thread.Sleep(2000);
    hub.Dispose();
    T.Between(mobo.Updates, 17, 21, "placa-mãe a 100 ms");
    T.Between(ssd.Updates, 6, 8, "disco a 300 ms");
});

t.Run("Pausa nos benchmarks: nenhuma leitura enquanto suspenso", () =>
{
    var gpu = Fake.Hw("RTX 4060", HardwareType.GpuNvidia);
    using var hub = new SensorHub(new IHardware[] { gpu }, new SensorSchedule { Speedup = 10 });
    hub.Start();
    Thread.Sleep(500);
    hub.Suspended = true;
    Thread.Sleep(100); // termina a volta em andamento
    int before = gpu.Updates;
    Thread.Sleep(1500);
    T.Eq(before, gpu.Updates, "leituras durante a pausa");
    hub.Suspended = false;
    Thread.Sleep(300);
    hub.Dispose();
    T.True(gpu.Updates > before, "voltou a ler depois da pausa");
});

t.Run("Um dispositivo com erro não derruba os outros", () =>
{
    var bad = Fake.Hw("USB estranho", HardwareType.Cooler, throws: true);
    var cpu = Fake.Hw("CPU", HardwareType.Cpu);
    using var hub = new SensorHub(new IHardware[] { bad, cpu }, new SensorSchedule { Speedup = 10 });
    hub.Start();
    Thread.Sleep(1000);
    var st = hub.Latest!.Stats;
    hub.Dispose();
    T.True(cpu.Updates >= 8, "CPU continuou sendo lida");
    T.True(st.Failures >= 3, $"falhas contadas ({st.Failures})");
});

t.Run("Sub-hardware (Super I/O da placa-mãe) também é lido", () =>
{
    var superio = Fake.Hw("ITE IT8625", HardwareType.SuperIO);
    var mobo = Fake.Hw("B550M-PX", HardwareType.Motherboard, sub: new IHardware[] { superio });
    using var hub = new SensorHub(new IHardware[] { mobo }, new SensorSchedule { Speedup = 10 });
    hub.Start();
    Thread.Sleep(1000);
    hub.Dispose();
    T.True(superio.Updates >= 4, $"Super I/O lido {superio.Updates}x");
    T.True(hub.Latest!.Readings.Any(r => r.Hardware == "ITE IT8625"), "sensores do Super I/O no snapshot");
});

t.Run("Tela só é avisada do que mudou, e nada com a página fechada", () =>
{
    var row = new SensorRow { Id = "/x", Hardware = "CPU", Name = "Core (Tctl/Tdie)", Type = SensorType.Temperature };
    int events = 0;
    row.PropertyChanged += (_, _) => events++;
    row.Update(60, 40, 60);
    T.Eq(3, events, "primeiro valor: 3 avisos");
    row.Update(60, 40, 60);
    T.Eq(3, events, "mesmo valor: nenhum aviso novo");
    row.Update(61, 40, 61);
    T.Eq(5, events, "valor e máximo mudaram: 2 avisos");
    row.Update(62, 40, 62, notify: false);
    T.Eq(5, events, "página fechada: nenhum aviso");
    T.Eq(62f, row.RawValue, "valor atualizado mesmo sem aviso (overlay/testes usam)");
    row.NotifyAll();
    T.Eq(8, events, "página abriu: redesenha tudo");
});

t.Run("Busca em cache acha sensor que aparece depois", () =>
{
    var table = new SensorTable();
    var s1 = new[] { new SensorReading("/gpu/load/0", "RTX 4060", HardwareType.GpuNvidia, "GPU Core", SensorType.Load, 50, 0, 50) };
    table.Apply(new SensorSnapshot(1, s1, Fake.NoStats));
    T.True(table.Find("RTX 4060", SensorType.Temperature, "GPU Core") == null, "ainda não existe");
    var s2 = s1.Append(new SensorReading("/gpu/temp/0", "RTX 4060", HardwareType.GpuNvidia, "GPU Core", SensorType.Temperature, 66, 40, 66)).ToArray();
    table.Apply(new SensorSnapshot(2, s2, Fake.NoStats));
    T.Eq(66f, table.Find("RTX 4060", SensorType.Temperature, "GPU Core")?.RawValue, "achou depois que apareceu");
    T.False(table.Apply(new SensorSnapshot(2, s2, Fake.NoStats)), "mesmo snapshot não é reaplicado");
});

t.Run("Temperatura e consumo da CPU: nomes reais do LibreHardwareMonitor (AMD e Intel)", () =>
{
    var amd = new SensorTable();
    amd.Apply(new SensorSnapshot(1, new[]
    {
        new SensorReading("/a/t/0", "AMD Ryzen 7 5700X", HardwareType.Cpu, "Core (Tctl/Tdie)", SensorType.Temperature, 78.9f, 40, 85.6f),
        new SensorReading("/a/t/1", "AMD Ryzen 7 5700X", HardwareType.Cpu, "CCD1 (Tdie)", SensorType.Temperature, 81f, 40, 86f),
        new SensorReading("/a/p/0", "AMD Ryzen 7 5700X", HardwareType.Cpu, "Package", SensorType.Power, 78.7f, 10, 91f),
        new SensorReading("/a/l/0", "AMD Ryzen 7 5700X", HardwareType.Cpu, "CPU Total", SensorType.Load, 47.9f, 0, 90),
    }, Fake.NoStats));
    T.Eq(78.9f, amd.CpuTemperature, "AMD usa Tctl/Tdie");
    T.Eq(78.7f, amd.CpuPower, "AMD Package");
    T.Eq(47.9f, amd.CpuLoad, "CPU Total");

    var intel = new SensorTable();
    intel.Apply(new SensorSnapshot(1, new[]
    {
        new SensorReading("/i/t/0", "Intel Core i5", HardwareType.Cpu, "CPU Core #1", SensorType.Temperature, 70, 30, 80),
        new SensorReading("/i/t/1", "Intel Core i5", HardwareType.Cpu, "CPU Package", SensorType.Temperature, 72, 30, 82),
        new SensorReading("/i/p/0", "Intel Core i5", HardwareType.Cpu, "CPU Package", SensorType.Power, 95, 10, 120),
    }, Fake.NoStats));
    T.Eq(72f, intel.CpuTemperature, "Intel usa CPU Package");
    T.Eq(95f, intel.CpuPower, "Intel CPU Package");

    var noPkg = new SensorTable();
    noPkg.Apply(new SensorSnapshot(1, new[]
    {
        new SensorReading("/c/t/0", "CPU", HardwareType.Cpu, "Core #1", SensorType.Temperature, 61, 30, 61),
        new SensorReading("/c/t/1", "CPU", HardwareType.Cpu, "Core #2", SensorType.Temperature, 64, 30, 64),
    }, Fake.NoStats));
    T.Eq(64f, noPkg.CpuTemperature, "sem Package: maior núcleo");
});

if (logPath != null && File.Exists(logPath))
{
    t.Run($"Replay do log real ({Path.GetFileName(logPath)})", () =>
    {
        var log = HwinfoLog.Load(logPath, "CPU (Tctl/Tdie) [°C]", "CPU PPT [W]", "Temperatura GPU [°C]", "Carga do núcleo da GPU [%]");
        var cpu = new ReplayHardware("AMD Ryzen 7 5700X", HardwareType.Cpu, log,
                                     ("Core (Tctl/Tdie)", SensorType.Temperature, 0), ("Package", SensorType.Power, 1));
        var gpu = new ReplayHardware("NVIDIA GeForce RTX 4060", HardwareType.GpuNvidia, log,
                                     ("GPU Core", SensorType.Temperature, 2), ("GPU Core", SensorType.Load, 3));
        // Muito acelerado: cada leitura do hub avança uma linha do log (2 s no PC real).
        using var hub = new SensorHub(new IHardware[] { cpu, gpu }, new SensorSchedule { Speedup = 200 });
        var table = new SensorTable();
        hub.Start();
        float maxSeen = 0;
        var sw = Stopwatch.StartNew();
        while (!cpu.Finished && sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            table.Apply(hub.Latest);
            if (table.CpuTemperature is float c) maxSeen = Math.Max(maxSeen, c);
            Thread.Sleep(1);
        }
        Thread.Sleep(50);
        table.Apply(hub.Latest);
        hub.Dispose();

        float logMax = log.Max(r => r[0]);
        var tctl = table.Find("AMD Ryzen 7 5700X", SensorType.Temperature, "Core (Tctl/Tdie)")!;
        Console.WriteLine($"      {log.Count} amostras ({log.Count * 2 / 60.0:0} min de jogo) em {sw.Elapsed.TotalSeconds:0.0} s; " +
                          $"Tctl máx. no log {logMax:0.0} °C, máx. registrado {tctl.Max}, último {tctl.Value}");
        T.True(cpu.Finished, "leu o log inteiro");
        T.Eq(logMax, float.Parse(tctl.Max.Split(' ')[0], CultureInfo.CurrentCulture), "máximo da sessão preservado");
        T.True(maxSeen <= logMax, "nenhum valor inventado");
        T.Eq(log[^1][0], table.CpuTemperature, "termina no último valor do log");
        T.Eq(log[^1][3], table.Find("NVIDIA GeForce RTX 4060", SensorType.Load, "GPU Core")?.RawValue, "carga da GPU");
    });
}
else Console.WriteLine("  (sem log do HWiNFO: replay pulado. Passe o caminho de um .CSV como argumento.)");

return t.Finish();

// ------------------------------------------------------------------ infraestrutura

sealed class TestRunner
{
    private int _ok, _fail;
    public void Run(string name, Action body)
    {
        try { body(); _ok++; Console.WriteLine($"  OK    {name}"); }
        catch (Exception ex) { _fail++; Console.WriteLine($"  FALHOU {name}\n        {ex.Message}"); }
    }
    public int Finish()
    {
        Console.WriteLine($"\n{_ok} passaram, {_fail} falharam");
        return _fail == 0 ? 0 : 1;
    }
}

static class T
{
    public static void True(bool c, string what) { if (!c) throw new Exception(what); }
    public static void False(bool c, string what) => True(!c, what);
    public static void Eq<TV>(TV expected, TV actual, string what = "")
    { if (!EqualityComparer<TV>.Default.Equals(expected, actual)) throw new Exception($"{what}: esperado {expected}, veio {actual}"); }
    public static void Between(int v, int lo, int hi, string what) { if (v < lo || v > hi) throw new Exception($"{what}: {v} leituras (esperado {lo}–{hi})"); }
}

sealed class FakeSensor : ISensor
{
    public required Identifier Identifier { get; init; }
    public required string Name { get; set; }
    public required SensorType SensorType { get; init; }
    public float? Value { get; set; }
    public float? Min { get; set; }
    public float? Max { get; set; }
    public void Set(float v) { Value = v; Min = Min is null || v < Min ? v : Min; Max = Max is null || v > Max ? v : Max; }
}

class FakeHardware : IHardware
{
    private int _updates;
    public int Updates => Volatile.Read(ref _updates);
    public required HardwareType HardwareType { get; init; }
    public required string Name { get; set; }
    public ISensor[] Sensors { get; init; } = Array.Empty<ISensor>();
    public IHardware[] SubHardware { get; init; } = Array.Empty<IHardware>();
    public TimeSpan Cost { get; init; }
    public bool Throws { get; init; }
    public virtual void Update()
    {
        Interlocked.Increment(ref _updates);
        if (Cost > TimeSpan.Zero) Thread.Sleep(Cost);
        if (Throws) throw new IOException("dispositivo não respondeu");
        foreach (FakeSensor s in Sensors) s.Set((s.Value ?? 40) + 0.1f);
    }
}

sealed class ReplayHardware : FakeHardware
{
    private readonly List<float[]> _log;
    private readonly (FakeSensor S, int Col)[] _map;
    private int _row;
    public bool Finished => Volatile.Read(ref _row) >= _log.Count;

    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public ReplayHardware(string name, HardwareType type, List<float[]> log, params (string Name, SensorType Type, int Col)[] cols)
    {
        Name = name; HardwareType = type; _log = log;
        _map = cols.Select((c, i) => (new FakeSensor { Identifier = new Identifier(name, c.Type.ToString(), i.ToString()), Name = c.Name, SensorType = c.Type }, c.Col)).ToArray();
        Sensors = _map.Select(m => (ISensor)m.S).ToArray();
    }

    public override void Update()
    {
        int r = Volatile.Read(ref _row);
        if (r >= _log.Count) return;
        foreach (var (s, col) in _map) s.Set(_log[r][col]);
        Volatile.Write(ref _row, r + 1);
    }
}

static class Fake
{
    public static readonly SensorHubStats NoStats = new(0, null, 0, 0);
    public static FakeHardware Hw(string name, HardwareType type, TimeSpan cost = default, int sensors = 4, bool throws = false, IHardware[]? sub = null) => new()
    {
        Name = name, HardwareType = type, Cost = cost, Throws = throws, SubHardware = sub ?? Array.Empty<IHardware>(),
        Sensors = Enumerable.Range(0, sensors).Select(i => (ISensor)new FakeSensor
            { Identifier = new Identifier(name, "s", i.ToString()), Name = $"Sensor {i}", SensorType = SensorType.Temperature }).ToArray(),
    };
}

/// <summary>Lê colunas de um log CSV do HWiNFO (vírgula como separador, ponto decimal, rodapé repetindo o cabeçalho).</summary>
static class HwinfoLog
{
    public static List<float[]> Load(string path, params string[] columns)
    {
        var lines = File.ReadAllLines(path);
        var head = SplitCsv(lines[0]);
        var idx = columns.Select(c => Array.IndexOf(head, c)).ToArray();
        if (idx.Any(i => i < 0)) throw new Exception("coluna não encontrada: " + string.Join(", ", columns.Where((c, i) => idx[i] < 0)));
        var rows = new List<float[]>();
        foreach (var line in lines.Skip(1))
        {
            var f = line.Split(',');
            if (f.Length < head.Length - 1 || !char.IsDigit(f[1].FirstOrDefault())) continue; // rodapé
            var vals = new float[idx.Length];
            bool ok = true;
            for (int i = 0; i < idx.Length; i++)
                ok &= float.TryParse(f[idx[i]], NumberStyles.Float, CultureInfo.InvariantCulture, out vals[i]);
            if (ok) rows.Add(vals);
        }
        return rows;
    }

    private static string[] SplitCsv(string line)
    {
        var res = new List<string>(); var cur = new System.Text.StringBuilder(); bool q = false;
        foreach (char ch in line.TrimStart('﻿'))
        {
            if (ch == '"') q = !q;
            else if (ch == ',' && !q) { res.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        res.Add(cur.ToString());
        return res.ToArray();
    }
}
