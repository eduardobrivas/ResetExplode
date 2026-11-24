using System.Diagnostics;
using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

/// <summary>Leitura de um sensor num instante. Os textos vêm do LibreHardwareMonitor e são reaproveitados (sem cópia).</summary>
public readonly record struct SensorReading(string Id, string Hardware, HardwareType HardwareType, string Name, SensorType Type,
                                            float? Value, float? Min, float? Max);

/// <summary>Tudo o que foi lido até agora, publicado de uma vez (a tela nunca vê uma leitura pela metade).</summary>
public sealed record SensorSnapshot(long Version, SensorReading[] Readings, SensorHubStats Stats);

/// <summary>Quanto a coleta está custando. CostMsPerSecond = tempo da thread de sensores por segundo de relógio.</summary>
public sealed record SensorHubStats(double CostMsPerSecond, string? SlowestHardware, double SlowestMs, int Failures);

/// <summary>
/// Lê os sensores numa thread própria, cada tipo de hardware no seu ritmo (ver <see cref="SensorSchedule"/>).
/// A thread da janela só pega o último <see cref="SensorSnapshot"/> pronto: nunca espera o driver.
/// Todo acesso aos objetos do LibreHardwareMonitor acontece nesta thread depois de <see cref="Start"/>.
/// </summary>
public sealed class SensorHub : IDisposable
{
    private sealed class Entry
    {
        public required IHardware Hardware;
        public required SensorTier Tier;
        public TimeSpan? LastRead;
        public double LastCostMs;
    }

    private readonly IReadOnlyList<IHardware> _roots;
    private readonly SensorSchedule _schedule;
    private readonly TimeSpan _tick;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Dictionary<ISensor, string> _ids = new(ReferenceEqualityComparer.Instance);
    private readonly Queue<(TimeSpan At, double Ms)> _costs = new();
    private List<Entry> _entries = new();
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _suspended;
    private volatile SensorMode _mode = SensorMode.Normal;
    private volatile bool _detail;
    private SensorSnapshot? _latest;
    private long _version;
    private int _failures;

    public SensorHub(IReadOnlyList<IHardware> roots, SensorSchedule? schedule = null)
    {
        _roots = roots;
        _schedule = schedule ?? new SensorSchedule();
        _tick = TimeSpan.FromMilliseconds(250 / _schedule.Speedup);
    }

    /// <summary>Último conjunto completo de leituras (null até a primeira volta terminar).</summary>
    public SensorSnapshot? Latest => Volatile.Read(ref _latest);

    /// <summary>Quantas vezes cada hardware foi lido (para diagnóstico e testes).</summary>
    public IReadOnlyDictionary<string, int> ReadCounts => _readCounts;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _readCounts = new();

    /// <summary>Pausa total, usada durante os benchmarks: cada consulta ao driver de vídeo vira engasgo no 1% low.</summary>
    public bool Suspended
    {
        get => _suspended;
        set { _suspended = value; if (!value) Wake(); }
    }

    public SensorMode Mode
    {
        get => _mode;
        set { if (_mode == value) return; _mode = value; Wake(); }
    }

    /// <summary>Uma tela com a lista completa de sensores está aberta: ventoinhas e discos andam mais rápido.</summary>
    public bool DetailWanted
    {
        get => _detail;
        set { if (_detail == value) return; _detail = value; Wake(); }
    }

    /// <summary>Acorda a thread para aplicar uma mudança já (ignorado depois do Dispose).</summary>
    private void Wake() { if (!_stop) _wake.Set(); }

    public void Start()
    {
        if (_thread != null) return;
        _entries = Flatten();
        // Normal (e não BelowNormal): durante o stress os núcleos estão a 100% e a leitura de temperatura não pode atrasar.
        _thread = new Thread(Loop) { IsBackground = true, Name = "Sensores", Priority = ThreadPriority.Normal };
        _thread.Start();
    }

    private List<Entry> Flatten()
    {
        var list = new List<Entry>();
        void Add(IHardware hw, SensorTier tier)
        {
            list.Add(new Entry { Hardware = hw, Tier = tier });
            foreach (var sub in hw.SubHardware) Add(sub, tier); // Super I/O segue o ritmo da placa-mãe
        }
        foreach (var hw in _roots) Add(hw, SensorSchedule.TierOf(hw.HardwareType));
        return list;
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        while (!_stop)
        {
            if (!_suspended && RunCycle(clock.Elapsed)) Publish(clock.Elapsed);
            _wake.WaitOne(_tick);
        }
    }

    /// <summary>Lê o que está vencido. Retorna se leu alguma coisa.</summary>
    private bool RunCycle(TimeSpan now)
    {
        bool any = false;
        var mode = _mode;
        bool detail = _detail;
        foreach (var e in _entries)
        {
            if (_stop || _suspended) break;
            // Meia volta de tolerância: sem ela, uma leitura que acorda 1 ms antes do vencimento atrasaria uma volta inteira.
            var interval = _schedule.Interval(e.Tier, mode, detail) - _tick / 2;
            if (!SensorSchedule.IsDue(e.LastRead, now, interval)) continue;

            long t0 = Stopwatch.GetTimestamp();
            try { e.Hardware.Update(); }
            catch { Interlocked.Increment(ref _failures); } // um dispositivo com problema não derruba os outros
            e.LastCostMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            e.LastRead = now;
            _readCounts.AddOrUpdate(e.Hardware.Name, 1, (_, n) => n + 1);
            lock (_costs) _costs.Enqueue((now, e.LastCostMs));
            any = true;
        }
        return any;
    }

    private void Publish(TimeSpan now)
    {
        var readings = new List<SensorReading>(_ids.Count + 16);
        foreach (var e in _entries)
        {
            var hw = e.Hardware;
            foreach (var s in hw.Sensors)
            {
                if (!_ids.TryGetValue(s, out var id)) _ids[s] = id = s.Identifier.ToString();
                readings.Add(new SensorReading(id, hw.Name, hw.HardwareType, s.Name, s.SensorType, s.Value, s.Min, s.Max));
            }
        }

        double windowSec = 10;
        double total;
        lock (_costs)
        {
            while (_costs.Count > 0 && now - _costs.Peek().At > TimeSpan.FromSeconds(windowSec)) _costs.Dequeue();
            total = _costs.Sum(c => c.Ms);
        }
        double elapsed = Math.Min(windowSec, Math.Max(1, now.TotalSeconds));
        var slow = _entries.MaxBy(e => e.LastCostMs);
        var stats = new SensorHubStats(total / elapsed, slow?.Hardware.Name, slow?.LastCostMs ?? 0, Volatile.Read(ref _failures));

        Volatile.Write(ref _latest, new SensorSnapshot(Interlocked.Increment(ref _version), readings.ToArray(), stats));
    }

    /// <summary>Para a thread e espera ela soltar o hardware (antes de fechar o LibreHardwareMonitor).</summary>
    public void Dispose()
    {
        if (_stop) return; // chamar duas vezes não pode mexer no evento já descartado
        _stop = true;
        _wake.Set();
        // Se um driver travar a thread por mais de 3 s, não descarta o evento que ela ainda vai usar (derrubaria o app).
        if (_thread == null || _thread.Join(TimeSpan.FromSeconds(3))) _wake.Dispose();
    }
}
