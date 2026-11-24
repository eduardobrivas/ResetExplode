using System.Management;

namespace ResetExplode;

/// <summary>Gera carga sintética de CPU (todos os threads) e/ou testa a RAM (<see cref="MemTester"/>).</summary>
public sealed class StressTester
{
    private CancellationTokenSource? _cts;
    private readonly List<Thread> _threads = new();
    private MemTester? _mem;
    private long _ramRequested;

    public bool Running => _cts != null;

    /// <summary>Teste de RAM da última execução (continua disponível depois de parar, para o resumo).</summary>
    public MemTester? Memory => _mem;
    public long MemoryErrors => _mem?.Errors ?? 0;
    public long MemoryPasses => _mem?.Passes ?? 0;

    /// <summary>Memória em teste (o pedido enquanto as threads alocam, depois o que foi conseguido). 0 = RAM desligada.</summary>
    public long MemoryAllocatedBytes => _mem == null ? 0 : _mem.Ready ? _mem.AllocatedBytes : _ramRequested;

    /// <summary>RAM física instalada que o Windows enxerga (para mostrar a cobertura do teste).</summary>
    public static long InstalledMemoryBytes => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    /// <param name="cpu">Carga em todos os processadores lógicos.</param>
    /// <param name="ram">Aloca uma fração da RAM livre e grava/verifica padrões continuamente.</param>
    /// <param name="ramFraction">Fração da memória livre a usar (0.1 a 0.9).</param>
    public void Start(bool cpu, bool ram, double ramFraction = 0.6)
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _mem?.Dispose();
        _mem = null;
        _ramRequested = 0;

        int logical = Environment.ProcessorCount;
        // Só RAM: uma thread de teste por processador lógico (banda máxima, como o TestMem5).
        // CPU + RAM: metade para cada, para a CPU esquentar e a memória continuar sob carga pesada.
        int ramThreads = !ram ? 0 : cpu ? Math.Max(1, logical / 2) : logical;
        int cpuThreads = cpu ? Math.Max(1, logical - ramThreads) : 0;

        if (cpu)
            for (int i = 0; i < cpuThreads; i++)
            {
                int seed = i;
                StartThread(() => CpuWorker(token, seed), $"Stress CPU {seed}");
            }

        if (ram)
        {
            long bytes = (long)(FreePhysicalMemory() * Math.Clamp(ramFraction, 0.1, 0.9));
            // Versão de 32 bits: o processo só enxerga ~2 GB de memória; passar disso derrubaria o programa.
            if (!Environment.Is64BitProcess) bytes = Math.Min(bytes, 1L << 30);
            _ramRequested = bytes;
            _mem = new MemTester();
            // Prender cada thread a um núcleo só faz sentido quando a CPU está livre para o teste de RAM.
            _mem.Start(bytes, ramThreads, pinThreads: !cpu);
        }
    }

    public void Stop()
    {
        if (_cts == null) return;
        _cts.Cancel();
        _mem?.Stop(); // devolve a memória; contagens e erros continuam legíveis para o resumo
        foreach (var t in _threads) t.Join(2000);
        _threads.Clear();
        _cts.Dispose();
        _cts = null;
        GC.Collect();
    }

    private void StartThread(ThreadStart body, string name)
    {
        // BelowNormal mantém a interface responsiva sem reduzir a carga (núcleos continuam a 100%).
        var t = new Thread(body) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
        _threads.Add(t);
        t.Start();
    }

    private static void CpuWorker(CancellationToken token, int seed)
    {
        // Mistura ponto flutuante (FMA/sqrt/sin) e inteiros sobre um buffer que cabe no cache L1/L2.
        var buf = new double[4096];
        for (int i = 0; i < buf.Length; i++) buf[i] = i + seed + 1;
        ulong x = (ulong)(seed + 1) * 0x9E3779B97F4A7C15UL;

        while (!token.IsCancellationRequested)
        {
            for (int rep = 0; rep < 64; rep++)
            {
                for (int i = 0; i < buf.Length; i++)
                {
                    double v = buf[i];
                    v = Math.FusedMultiplyAdd(v, 1.0000001, 0.5);
                    v = Math.Sqrt(v * v + 1.0) + Math.Sin(v) * 1e-3;
                    buf[i] = v > 1e12 ? i + 1 : v;

                    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                }
            }
            if (x == 0) buf[0] = 0; // evita que o JIT elimine o cálculo inteiro
        }
    }

    private static long FreePhysicalMemory()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in s.Get())
                return Convert.ToInt64(mo["FreePhysicalMemory"]) * 1024; // vem em KB
        }
        catch { }
        return 1L << 30;
    }
}
