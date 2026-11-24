using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ResetExplode;

/// <summary>Um erro encontrado: o que devia estar na memória e o que estava.</summary>
public sealed record MemError(string Test, int Thread, long OffsetBytes, ulong Expected, ulong Actual)
{
    public int BitsFlipped => BitOperations.PopCount(Expected ^ Actual);
}

/// <summary>Um dos padrões do teste e o tipo de defeito que ele pega.</summary>
public sealed record MemTestInfo(string Name, string Detects);

/// <summary>
/// Teste de estabilidade da RAM no estilo TestMem5/MemTest86, rodando dentro do Windows:
/// várias threads, cada uma com sua faixa de memória travada na RAM (VirtualLock, para o Windows não
/// mandar para o disco), e seis padrões que pegam defeitos diferentes. A escrita dos padrões fixos usa
/// "non-temporal stores" (AVX), que vão direto para a RAM sem passar pelo cache.
/// Limite: só testa a memória que o Windows libera (normalmente 85-90%), não a área do próprio sistema.
/// </summary>
public sealed unsafe class MemTester : IDisposable
{
    public static readonly MemTestInfo[] Tests =
    {
        new("Endereço próprio", "endereçamento (linhas/colunas trocadas, alias)"),
        new("Inversões 0/1", "bits presos em 0 ou 1 e acoplamento entre células"),
        new("Inversões 55/AA", "interferência entre bits vizinhos"),
        new("Walking 1/0", "bits individuais fracos"),
        new("Aleatório", "erros que dependem dos dados (timings no limite)"),
        new("Cópia em bloco", "estabilidade com banda máxima (controlador de memória, Infinity Fabric)"),
    };

    /// <summary>
    /// Só para testes automáticos: chamado depois que um teste escreve e antes de conferir,
    /// para simular um defeito (trocar bits, copiar um endereço em outro...).
    /// </summary>
    internal static Action<string, int, IntPtr, long>? FaultInjection;

    private const long ChunkWords = 1 << 17; // 1 MB: cancelamento e progresso a cada bloco
    private const int MaxKeptErrors = 64;

    private readonly List<Thread> _threads = new();
    private CancellationTokenSource? _cts;
    private Region?[] _regions = Array.Empty<Region?>();
    private long[] _passes = Array.Empty<long>();
    private int[] _testIndex = Array.Empty<int>();
    private double[] _testFrac = Array.Empty<double>();
    private readonly long[] _errorsByTest = new long[Tests.Length];
    private readonly ConcurrentQueue<MemError> _firstErrors = new();
    private long _errors, _bytesProcessed, _allocated;
    private bool[] _active = Array.Empty<bool>(); // threads que conseguiram memória (continua valendo depois de parar)
    private int _ready, _lockedCount;
    private readonly Stopwatch _clock = new();
    private WorkingSet? _ws;

    public bool Running => _cts != null && _threads.Any(t => t.IsAlive);
    public int ThreadCount => _threads.Count;
    public long Errors => Interlocked.Read(ref _errors);
    public long ErrorsInTest(int i) => Interlocked.Read(ref _errorsByTest[i]);
    public IReadOnlyCollection<MemError> FirstErrors => _firstErrors;

    /// <summary>Ciclos completos (todos os padrões) feitos pela thread mais atrasada.</summary>
    public long Passes
    {
        get
        {
            long min = long.MaxValue;
            for (int t = 0; t < _passes.Length; t++)
                if (_active[t]) min = Math.Min(min, Interlocked.Read(ref _passes[t]));
            return min == long.MaxValue ? 0 : min; // threads que não conseguiram memória não contam
        }
    }

    /// <summary>Progresso do ciclo atual (0 a 1), média das threads.</summary>
    public double PassProgress
    {
        get
        {
            if (_testIndex.Length == 0) return 0;
            double sum = 0;
            for (int t = 0; t < _testIndex.Length; t++) sum += (Volatile.Read(ref _testIndex[t]) + Volatile.Read(ref _testFrac[t])) / Tests.Length;
            return sum / _testIndex.Length;
        }
    }

    /// <summary>Padrão em que a maioria das threads está agora.</summary>
    public string CurrentTest => _testIndex.Length == 0 ? "—"
        : Tests[_testIndex.GroupBy(i => i).MaxBy(g => g.Count())!.Key % Tests.Length].Name;

    /// <summary>Memória realmente alocada (pode ser menos que o pedido se o Windows negar).</summary>
    public long AllocatedBytes => Interlocked.Read(ref _allocated);

    /// <summary>Todas as threads conseguiram alocar (o teste começou de fato)?</summary>
    public bool Ready => Volatile.Read(ref _ready) == _threads.Count && _threads.Count > 0;

    /// <summary>Quantas faixas ficaram travadas na RAM (se não travar, o Windows pode mandar parte para o disco).</summary>
    public int LockedRegions => Volatile.Read(ref _lockedCount);

    /// <summary>Volume lido + escrito por segundo, somando as threads (GB/s).</summary>
    public double ThroughputGBs => _clock.Elapsed.TotalSeconds < 0.5 ? 0 : Interlocked.Read(ref _bytesProcessed) / _clock.Elapsed.TotalSeconds / 1e9;

    /// <param name="totalBytes">Memória a testar, dividida entre as threads.</param>
    /// <param name="threads">Threads de teste (normalmente uma por processador lógico).</param>
    /// <param name="pinThreads">Prende cada thread a um processador lógico (só quando ninguém mais está usando a CPU).</param>
    /// <param name="maxPasses">0 = até parar.</param>
    public void Start(long totalBytes, int threads, bool pinThreads = true, int maxPasses = 0, bool lockMemory = true)
    {
        if (_cts != null) throw new InvalidOperationException("teste já iniciado");
        threads = Math.Max(1, threads);
        long per = Math.Max(16L << 20, totalBytes / threads / (2 << 20) * (2 << 20)); // múltiplo de 2 MB, mínimo 16 MB
        _cts = new CancellationTokenSource();
        _regions = new Region?[threads];
        _passes = new long[threads];
        _testIndex = new int[threads];
        _testFrac = new double[threads];
        _active = new bool[threads];
        if (lockMemory && OperatingSystem.IsWindows()) _ws = WorkingSet.Raise(per * threads);
        _clock.Restart();
        var ct = _cts.Token;
        for (int t = 0; t < threads; t++)
        {
            int idx = t;
            var th = new Thread(() => Worker(idx, per, pinThreads && threads <= 64, maxPasses, lockMemory, ct))
            { IsBackground = true, Name = $"Teste RAM {t}", Priority = ThreadPriority.BelowNormal };
            _threads.Add(th);
        }
        foreach (var th in _threads) th.Start();
    }

    /// <summary>Espera todas as threads terminarem (útil com maxPasses).</summary>
    public bool Wait(TimeSpan timeout) => _threads.All(t => t.Join(timeout));

    public void Stop()
    {
        if (_cts == null) return;
        _cts.Cancel();
        for (int t = 0; t < _threads.Count; t++)
        {
            // Só devolve a memória de uma thread que terminou: liberar com ela ainda escrevendo derrubaria o app.
            if (_threads[t].Join(5000)) _regions[t]?.Dispose();
        }
        _threads.Clear();
        _regions = Array.Empty<Region?>();
        _ws?.Dispose();
        _ws = null;
        _cts.Dispose();
        _cts = null;
        _clock.Stop();
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------ thread de teste

    private void Worker(int t, long bytes, bool pin, int maxPasses, bool lockMemory, CancellationToken ct)
    {
        if (pin && OperatingSystem.IsWindows())
            SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << (t % Environment.ProcessorCount)));

        // Cada thread aloca a própria faixa: em paralelo (travar 20 GB leva segundos) e perto do seu núcleo.
        Region? r = null;
        for (long b = bytes; b >= (16L << 20) && r == null && !ct.IsCancellationRequested; b /= 2)
        {
            try { r = Region.Allocate(b, lockMemory); }
            catch (OutOfMemoryException) { }
        }
        if (r == null) { Interlocked.Increment(ref _ready); return; }
        _regions[t] = r;
        _active[t] = true;
        Interlocked.Add(ref _allocated, r.Bytes);
        if (r.Locked) Interlocked.Increment(ref _lockedCount);
        Interlocked.Increment(ref _ready);

        ulong* p = r.Pointer;
        long n = r.Words;
        ulong pass = 0;
        while (!ct.IsCancellationRequested && (maxPasses == 0 || (long)pass < maxPasses))
        {
            for (int i = 0; i < Tests.Length && !ct.IsCancellationRequested; i++)
            {
                Volatile.Write(ref _testIndex[t], i);
                Volatile.Write(ref _testFrac[t], 0);
                RunTest(i, p, n, t, pass, ct);
            }
            if (ct.IsCancellationRequested) break;
            pass++;
            Interlocked.Increment(ref _passes[t]);
        }
        Volatile.Write(ref _testIndex[t], 0);
    }

    private void RunTest(int i, ulong* p, long n, int t, ulong pass, CancellationToken ct)
    {
        var sink = new Sink(this, i, t, p);
        switch (i)
        {
            case 0: OwnAddress(p, n, pass, sink, ct); break;
            case 1: MovingInversions(p, n, pass % 2 == 0 ? 0UL : ~0UL, sink, ct); break;
            case 2: MovingInversions(p, n, pass % 2 == 0 ? 0x5555_5555_5555_5555UL : 0xAAAA_AAAA_AAAA_AAAAUL, sink, ct); break;
            case 3: Walking(p, n, pass, sink, ct); break;
            case 4: RandomFill(p, n, Seed(t, pass), sink, ct); break;
            case 5: BlockCopy(p, n, Seed(t, pass) ^ 0xC0FFEE, sink, ct); break;
        }
    }

    private static ulong Seed(int t, ulong pass) => (pass + 1) * 0x9E3779B97F4A7C15UL ^ ((ulong)t + 1) * 0xD1B54A32D192ED03UL;

    /// <summary>Coleta os erros de um teste (contagem por padrão e os primeiros com detalhes).</summary>
    private readonly struct Sink
    {
        private readonly MemTester _m; private readonly int _test, _thread; private readonly ulong* _base;
        public Sink(MemTester m, int test, int thread, ulong* b) { _m = m; _test = test; _thread = thread; _base = b; }
        public string Name => Tests[_test].Name;
        public int Thread => _thread;
        public void Error(ulong* at, ulong expected, ulong actual)
        {
            Interlocked.Increment(ref _m._errors);
            Interlocked.Increment(ref _m._errorsByTest[_test]);
            if (_m._firstErrors.Count < MaxKeptErrors)
                _m._firstErrors.Enqueue(new MemError(Tests[_test].Name, _thread, (long)((byte*)at - (byte*)_base), expected, actual));
        }
        public void Progress(double f) => Volatile.Write(ref _m._testFrac[_thread], f);
        public void Bytes(long b) => Interlocked.Add(ref _m._bytesProcessed, b);
        public void Inject(ulong* p, long n) => FaultInjection?.Invoke(Name, _thread, (IntPtr)p, n);
    }

    // ------------------------------------------------------------------ padrões

    /// <summary>Cada palavra guarda o próprio endereço (invertido nos ciclos ímpares). Pega endereçamento trocado.</summary>
    private static void OwnAddress(ulong* p, long n, ulong pass, Sink s, CancellationToken ct)
    {
        ulong mix = pass % 2 == 0 ? 0UL : ~0UL;
        for (long c = 0; c < n && !ct.IsCancellationRequested; c += ChunkWords)
        {
            long end = Math.Min(n, c + ChunkWords);
            for (long i = c; i < end; i++) p[i] = (ulong)(p + i) ^ mix;
            s.Bytes((end - c) * 8); s.Progress(0.5 * end / n);
        }
        s.Inject(p, n);
        for (long c = 0; c < n && !ct.IsCancellationRequested; c += ChunkWords)
        {
            long end = Math.Min(n, c + ChunkWords);
            for (long i = c; i < end; i++)
            {
                ulong exp = (ulong)(p + i) ^ mix;
                if (p[i] != exp) s.Error(p + i, exp, p[i]);
            }
            s.Bytes((end - c) * 8); s.Progress(0.5 + 0.5 * end / n);
        }
    }

    /// <summary>
    /// Moving inversions (MemTest86): escreve o padrão; sobe conferindo e gravando o inverso; desce conferindo
    /// o inverso e gravando o padrão; confere no fim. A ordem de subida/descida pega acoplamento entre células.
    /// </summary>
    private static void MovingInversions(ulong* p, long n, ulong pat, Sink s, CancellationToken ct)
    {
        ulong inv = ~pat;
        for (long c = 0; c < n && !ct.IsCancellationRequested; c += ChunkWords)
        {
            long len = Math.Min(ChunkWords, n - c);
            Fill(p + c, len, pat);
            s.Bytes(len * 8); s.Progress(0.25 * (c + len) / n);
        }
        s.Inject(p, n);
        for (long c = 0; c < n && !ct.IsCancellationRequested; c += ChunkWords)
        {
            long len = Math.Min(ChunkWords, n - c);
            CheckAndWrite(p + c, len, pat, inv, s);
            s.Bytes(len * 16); s.Progress(0.25 + 0.25 * (c + len) / n);
        }
        s.Inject(p, n);
        for (long c = n; c > 0 && !ct.IsCancellationRequested; c -= ChunkWords)
        {
            long start = Math.Max(0, c - ChunkWords);
            CheckAndWriteDown(p + start, c - start, inv, pat, s);
            s.Bytes((c - start) * 16); s.Progress(0.5 + 0.25 * (n - start) / n);
        }
        s.Inject(p, n);
        for (long c = 0; c < n && !ct.IsCancellationRequested; c += ChunkWords)
        {
            long len = Math.Min(ChunkWords, n - c);
            Check(p + c, len, pat, s);
            s.Bytes(len * 8); s.Progress(0.75 + 0.25 * (c + len) / n);
        }
    }

    /// <summary>Um bit 1 andando pela palavra (e depois um 0 andando), deslocado a cada palavra e a cada ciclo.</summary>
    private static void Walking(ulong* p, long n, ulong pass, Sink s, CancellationToken ct)
    {
        for (int phase = 0; phase < 2; phase++)
        {
            ulong flip = phase == 0 ? 0UL : ~0UL;
            int shift = (int)(pass % 64);
            for (long c = 0; c < n && !ct.IsCancellationRequested; c += ChunkWords)
            {
                long end = Math.Min(n, c + ChunkWords);
                for (long i = c; i < end; i++) p[i] = BitOperations.RotateLeft(1UL, (int)((i + shift) & 63)) ^ flip;
                s.Bytes((end - c) * 8); s.Progress(phase * 0.5 + 0.25 * end / n);
            }
            s.Inject(p, n);
            for (long c = 0; c < n && !ct.IsCancellationRequested; c += ChunkWords)
            {
                long end = Math.Min(n, c + ChunkWords);
                for (long i = c; i < end; i++)
                {
                    ulong exp = BitOperations.RotateLeft(1UL, (int)((i + shift) & 63)) ^ flip;
                    if (p[i] != exp) s.Error(p + i, exp, p[i]);
                }
                s.Bytes((end - c) * 8); s.Progress(phase * 0.5 + 0.25 + 0.25 * end / n);
            }
        }
    }

    /// <summary>Sequência pseudoaleatória (xorshift), regenerada para conferir. Semente muda a cada ciclo e thread.</summary>
    private static void RandomFill(ulong* p, long n, ulong seed, Sink s, CancellationToken ct)
    {
        ulong x = seed | 1;
        for (long c = 0; c < n && !ct.IsCancellationRequested; c += ChunkWords)
        {
            long end = Math.Min(n, c + ChunkWords);
            for (long i = c; i < end; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; p[i] = x; }
            s.Bytes((end - c) * 8); s.Progress(0.5 * end / n);
        }
        s.Inject(p, n);
        x = seed | 1;
        for (long c = 0; c < n && !ct.IsCancellationRequested; c += ChunkWords)
        {
            long end = Math.Min(n, c + ChunkWords);
            for (long i = c; i < end; i++)
            {
                x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                if (p[i] != x) s.Error(p + i, x, p[i]);
            }
            s.Bytes((end - c) * 8); s.Progress(0.5 + 0.5 * end / n);
        }
    }

    /// <summary>
    /// Preenche a primeira metade com dados aleatórios, copia em blocos grandes para a segunda (leitura e escrita
    /// ao mesmo tempo, na banda máxima) e confere as duas metades.
    /// </summary>
    private static void BlockCopy(ulong* p, long n, ulong seed, Sink s, CancellationToken ct)
    {
        long half = n / 2 / 4 * 4;
        if (half == 0) return;
        ulong x = seed | 1;
        for (long i = 0; i < half; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; p[i] = x; }
        s.Bytes(half * 8); s.Progress(0.25);
        for (long c = 0; c < half && !ct.IsCancellationRequested; c += ChunkWords)
        {
            long len = Math.Min(ChunkWords, half - c);
            Buffer.MemoryCopy(p + c, p + half + c, len * 8, len * 8);
            s.Bytes(len * 16);
        }
        s.Progress(0.5);
        s.Inject(p, n);
        x = seed | 1;
        for (long i = 0; i < half && !ct.IsCancellationRequested; i++)
        {
            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            if (p[i] != x) s.Error(p + i, x, p[i]);
            if (p[half + i] != x) s.Error(p + half + i, x, p[half + i]);
        }
        s.Bytes(half * 16); s.Progress(1);
    }

    // ------------------------------------------------------------------ rotinas rápidas (AVX quando houver)

    /// <summary>Escreve um valor fixo. Com AVX, usa escrita que não passa pelo cache (vai direto para a RAM).</summary>
    private static void Fill(ulong* p, long n, ulong v)
    {
        long i = 0;
        if (Avx.IsSupported && ((nint)p & 31) == 0)
        {
            var vv = Vector256.Create(v);
            for (; i + 8 <= n; i += 8)
            {
                Avx.StoreAlignedNonTemporal((byte*)(p + i), vv.AsByte());
                Avx.StoreAlignedNonTemporal((byte*)(p + i + 4), vv.AsByte());
            }
            Sse.StoreFence();
        }
        for (; i < n; i++) p[i] = v;
    }

    private static void Check(ulong* p, long n, ulong v, Sink s)
    {
        long i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var vv = Vector256.Create(v);
            for (; i + 4 <= n; i += 4)
                if (Vector256.Load(p + i) != vv)
                    for (long k = i; k < i + 4; k++) if (p[k] != v) s.Error(p + k, v, p[k]);
        }
        for (; i < n; i++) if (p[i] != v) s.Error(p + i, v, p[i]);
    }

    /// <summary>Sobe: confere <paramref name="expect"/> e grava <paramref name="write"/>.</summary>
    private static void CheckAndWrite(ulong* p, long n, ulong expect, ulong write, Sink s)
    {
        long i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var ve = Vector256.Create(expect); var vw = Vector256.Create(write);
            for (; i + 4 <= n; i += 4)
            {
                if (Vector256.Load(p + i) != ve)
                    for (long k = i; k < i + 4; k++) if (p[k] != expect) s.Error(p + k, expect, p[k]);
                vw.Store(p + i);
            }
        }
        for (; i < n; i++) { if (p[i] != expect) s.Error(p + i, expect, p[i]); p[i] = write; }
    }

    /// <summary>Desce (do fim para o começo): confere e grava.</summary>
    private static void CheckAndWriteDown(ulong* p, long n, ulong expect, ulong write, Sink s)
    {
        long i = n;
        if (Vector256.IsHardwareAccelerated)
        {
            var ve = Vector256.Create(expect); var vw = Vector256.Create(write);
            for (; i - 4 >= 0; i -= 4)
            {
                if (Vector256.Load(p + i - 4) != ve)
                    for (long k = i - 1; k >= i - 4; k--) if (p[k] != expect) s.Error(p + k, expect, p[k]);
                vw.Store(p + i - 4);
            }
        }
        for (i--; i >= 0; i--) { if (p[i] != expect) s.Error(p + i, expect, p[i]); p[i] = write; }
    }

    // ------------------------------------------------------------------ memória

    /// <summary>Faixa de memória fora do coletor do .NET (nada se move, nada é zerado no meio do teste).</summary>
    private sealed class Region : IDisposable
    {
        public ulong* Pointer { get; private init; }
        public long Bytes { get; private init; }
        public long Words => Bytes / 8;
        public bool Locked { get; private init; }

        public static Region Allocate(long bytes, bool tryLock)
        {
            if (OperatingSystem.IsWindows())
            {
                void* ptr = VirtualAlloc(null, (UIntPtr)bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (ptr == null) throw new OutOfMemoryException();
                bool locked = tryLock && VirtualLock(ptr, (UIntPtr)bytes);
                return new Region { Pointer = (ulong*)ptr, Bytes = bytes, Locked = locked };
            }
            void* q = NativeMemory.AlignedAlloc((nuint)bytes, 4096);
            if (q == null) throw new OutOfMemoryException();
            return new Region { Pointer = (ulong*)q, Bytes = bytes, Locked = false };
        }

        private bool _freed;

        public void Dispose()
        {
            if (Pointer == null || _freed) return;
            _freed = true;
            if (OperatingSystem.IsWindows())
            {
                if (Locked) VirtualUnlock(Pointer, (UIntPtr)Bytes);
                VirtualFree(Pointer, UIntPtr.Zero, MEM_RELEASE);
            }
            else NativeMemory.AlignedFree(Pointer);
        }
    }

    /// <summary>
    /// O Windows só deixa travar na RAM o que cabe no "working set" do processo. Aumenta o limite durante o teste
    /// e devolve o original no fim. Se o Windows negar, o teste roda sem travar (e avisa).
    /// </summary>
    private sealed class WorkingSet : IDisposable
    {
        private UIntPtr _min, _max;
        private uint _flags;

        public static WorkingSet? Raise(long bytes)
        {
            var h = GetCurrentProcess();
            if (!GetProcessWorkingSetSizeEx(h, out var min, out var max, out var flags)) return null;
            ulong want = (ulong)bytes + (256UL << 20);
            if (!SetProcessWorkingSetSizeEx(h, (UIntPtr)want, (UIntPtr)(want + (512UL << 20)),
                                            QUOTA_LIMITS_HARDWS_MIN_DISABLE | QUOTA_LIMITS_HARDWS_MAX_DISABLE))
                return null;
            return new WorkingSet { _min = min, _max = max, _flags = flags };
        }

        public void Dispose() => SetProcessWorkingSetSizeEx(GetCurrentProcess(), _min, _max, _flags);
    }

    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, PAGE_READWRITE = 0x04;
    private const uint QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x2, QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x8;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern void* VirtualAlloc(void* addr, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFree(void* addr, UIntPtr size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualLock(void* addr, UIntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualUnlock(void* addr, UIntPtr size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessWorkingSetSizeEx(IntPtr proc, out UIntPtr min, out UIntPtr max, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr proc, UIntPtr min, UIntPtr max, uint flags);
}
