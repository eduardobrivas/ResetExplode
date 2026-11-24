using System.Diagnostics;
using System.Runtime.Intrinsics.X86;
using ResetExplode;

// Testes do motor do teste de RAM. Defeitos são simulados trocando bits entre a escrita e a conferência.
// Sai com código 1 se algum falhar.
const long Mb = 1 << 20;
int ok = 0, fail = 0;
void Run(string name, Action body)
{
    MemTester.FaultInjection = null;
    try { body(); ok++; Console.WriteLine($"  OK    {name}"); }
    catch (Exception ex) { fail++; Console.WriteLine($"  FALHOU {name}\n        {ex.Message}"); }
    finally { MemTester.FaultInjection = null; }
}
void Check(bool c, string what) { if (!c) throw new Exception(what); }

MemTester RunOnce(long bytes = 64 * Mb, int threads = 2)
{
    var m = new MemTester();
    m.Start(bytes, threads, pinThreads: false, maxPasses: 1, lockMemory: false);
    Check(m.Wait(TimeSpan.FromSeconds(120)), "o teste não terminou em 2 min");
    return m;
}

Console.WriteLine($"  (AVX: {(Avx.IsSupported ? "sim, escrita sem cache ativa" : "não, caminho comum")}, {Environment.ProcessorCount} processadores)");

Run("Memória boa: um ciclo completo sem nenhum erro", () =>
{
    using var m = RunOnce();
    Check(m.Errors == 0, $"{m.Errors} erros numa memória sem defeito");
    Check(m.Passes == 1, $"ciclos: {m.Passes}");
    Check(m.AllocatedBytes == 64 * Mb, $"alocado {m.AllocatedBytes / Mb} MB");
});

Run("Cada padrão acha um bit trocado, no lugar certo", () =>
{
    foreach (var test in MemTester.Tests)
    {
        long target = -1;
        MemTester.FaultInjection = (name, thread, p, words) =>
        {
            if (name != test.Name || thread != 0) return;
            unsafe
            {
                var w = (ulong*)p;
                target = words / 3 / 4 * 4 + 1;
                w[target] ^= 1UL << 13; // um bit troca de valor
            }
        };
        using var m = RunOnce(32 * Mb, 1);
        int idx = Array.IndexOf(MemTester.Tests, test);
        Check(m.ErrorsInTest(idx) >= 1, $"{test.Name}: não detectou");
        var e = m.FirstErrors.First(x => x.Test == test.Name);
        Check(e.BitsFlipped == 1, $"{test.Name}: {e.BitsFlipped} bits no erro (esperado 1)");
        Check(e.OffsetBytes == target * 8, $"{test.Name}: posição {e.OffsetBytes} (esperado {target * 8})");
        Check(MemTester.Tests.Where((_, i) => i != idx).All(o => m.ErrorsInTest(Array.IndexOf(MemTester.Tests, o)) == 0), $"{test.Name}: erro apareceu em outro padrão");
    }
});

// Matriz: que padrão pega que tipo de defeito. Mostra por que um padrão só (como o teste antigo) não basta.
var faults = new (string Name, Func<ulong, ulong> Apply, bool Alias)[]
{
    ("bit preso em 0", v => v & ~(1UL << 21), false),
    ("bit preso em 1", v => v | (1UL << 42), false),
    ("endereço duplicado", v => v, true), // a palavra B recebe o conteúdo da palavra A (decodificação de endereço com defeito)
};
var matrix = new bool[faults.Length, MemTester.Tests.Length];
Run("Matriz de detecção (bit preso em 0/1, endereço duplicado)", () =>
{
    for (int f = 0; f < faults.Length; f++)
    {
        var fault = faults[f];
        MemTester.FaultInjection = (name, thread, p, words) =>
        {
            unsafe
            {
                var w = (ulong*)p;
                for (long i = 1000; i + 4096 < words; i += 99_991) // um defeito a cada ~800 KB
                {
                    if (fault.Alias) w[i + 4096] = w[i];
                    else w[i] = fault.Apply(w[i]);
                }
            }
        };
        using var m = RunOnce(32 * Mb, 1);
        for (int t = 0; t < MemTester.Tests.Length; t++) matrix[f, t] = m.ErrorsInTest(t) > 0;
        Check(Enumerable.Range(0, MemTester.Tests.Length).Any(t => matrix[f, t]), $"{fault.Name}: nenhum padrão detectou");
    }
    Check(matrix[2, 0], "endereço duplicado tem que ser pego pelo 'Endereço próprio'");
    Check(matrix[0, 1] && matrix[1, 1], "inversões 0/1 tem que pegar bit preso nos dois sentidos");
});
Console.WriteLine();
Console.WriteLine("        " + string.Join(" | ", MemTester.Tests.Select(t => t.Name.PadRight(16))));
for (int f = 0; f < faults.Length; f++)
    Console.WriteLine("        " + string.Join(" | ", Enumerable.Range(0, MemTester.Tests.Length).Select(t => (matrix[f, t] ? "pega" : "  —").PadRight(16))) + "  ← " + faults[f].Name);
Console.WriteLine();

Run("Parar responde rápido mesmo com muita memória", () =>
{
    var m = new MemTester();
    m.Start(512 * Mb, 2, pinThreads: false, lockMemory: false);
    var w = Stopwatch.StartNew();
    while (!m.Ready && w.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(10);
    Thread.Sleep(800);
    var sw = Stopwatch.StartNew();
    m.Stop();
    Console.WriteLine($"      parou em {sw.ElapsedMilliseconds} ms; banda durante o teste {m.ThroughputGBs:0.0} GB/s, padrão atual {m.CurrentTest}");
    Check(sw.ElapsedMilliseconds < 1500, $"demorou {sw.ElapsedMilliseconds} ms para parar");
    Check(!m.Running, "threads ainda rodando");
});

// No Linux a memória é prometida e só cobrada no uso (overcommit): este caso só faz sentido no Windows.
if (OperatingSystem.IsWindows()) Run("Pedido maior que a memória disponível: aloca menos em vez de travar", () =>
{
    var m = new MemTester();
    long huge = 64L << 40; // 64 TB
    m.Start(huge, 1, pinThreads: false, lockMemory: false);
    var w = Stopwatch.StartNew();
    while (!m.Ready && w.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(10);
    long got = m.AllocatedBytes;
    m.Stop();
    Console.WriteLine($"      pediu 64 TB, conseguiu {got / (double)(1L << 30):0.0} GB");
    Check(m.Ready, "não terminou de alocar");
    Check(got > 0 && got < huge, "devia alocar menos que o pedido");
});

Run("Progresso e contagem de ciclos", () =>
{
    var m = new MemTester();
    m.Start(32 * Mb, 2, pinThreads: false, maxPasses: 3, lockMemory: false);
    double maxProg = 0;
    while (m.Running) { maxProg = Math.Max(maxProg, m.PassProgress); Thread.Sleep(5); }
    m.Wait(TimeSpan.FromSeconds(60));
    Check(m.Passes == 3, $"ciclos {m.Passes}");
    Check(maxProg > 0.3 && maxProg <= 1.0001, $"progresso máximo {maxProg:0.00}");
    m.Stop();
});

Console.WriteLine($"\n{ok} passaram, {fail} falharam");
return fail == 0 ? 0 : 1;
