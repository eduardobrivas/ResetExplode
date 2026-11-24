using System.Text;
using ResetExplode;

// Monta dois relatórios: um "aprovado" (CPU+RAM, 30 min simulados) e um "reprovado" (RAM com erros injetados),
// salva PDF e CSV e confere o básico. A conferência visual é feita renderizando os PDFs.
string outDir = args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "relatorios");
Directory.CreateDirectory(outDir);
int fail = 0;
void Check(bool c, string what) { if (!c) { fail++; Console.WriteLine("  FALHOU " + what); } else Console.WriteLine("  OK    " + what); }

RamResult RamRun(bool inject)
{
    if (inject)
        MemTester.FaultInjection = (name, thread, p, words) =>
        {
            if (name != "Aleatório" || thread != 1) return;
            unsafe { var w = (ulong*)p; w[words / 2] ^= 1UL << 7; w[words / 2 + 999] ^= 0b101UL << 30; }
        };
    using var m = new MemTester();
    m.Start(64L << 20, 2, pinThreads: false, maxPasses: 1, lockMemory: false);
    m.Wait(TimeSpan.FromSeconds(60));
    var r = new RamResult(m.AllocatedBytes / 1073741824.0, 31.9, m.ThreadCount, m.LockedRegions, m.Passes, m.Errors, m.ThroughputGBs,
                          MemTester.Tests.Select((t, i) => (t.Name, t.Detects, m.ErrorsInTest(i))).ToList(), m.FirstErrors.ToList());
    MemTester.FaultInjection = null;
    return r;
}

StressReport Build(bool fail, int seconds)
{
    var start = new DateTime(2026, 11, 24, 21, 0, 0);
    var r = new StressReport
    {
        Start = start, Cpu = !fail, Ram = true, Gpu = !fail, CpuLimit = 90, GpuLimit = 85, RamFraction = 0.9, DurationMinutes = 30,
        GpuSettings = "1920x1080, MSAA 4x, Alta, limite 85 °C",
        CpuName = "AMD Ryzen 7 5700X", GpuName = "NVIDIA GeForce RTX 4060", InstalledRamGB = 31.9,
        Os = "Windows 11 Pro 24H2 (26100)", AppVersion = "0.17.0-fork",
    };
    r.AddLog(start, $"Iniciado · CPU {(fail ? "não" : "sim (limite 90 °C)")} · RAM sim (90% da livre) · GPU {(fail ? "não" : "sim")} · 30 min");
    r.AddLog(start.AddSeconds(4), "RAM: testando 21,4 GB (67% da RAM) em 16 threads · travada na RAM: sim · 6 padrões por ciclo");
    var rnd = new Random(1);
    double cpuT = 45, gpuT = 40;
    for (int s = 0; s < seconds; s++)
    {
        cpuT += (80 - cpuT) * 0.02 + rnd.NextDouble() - 0.5;
        gpuT += (71 - gpuT) * 0.015 + (rnd.NextDouble() - 0.5) * 0.6;
        bool gap = s is > 300 and < 306; // sensor sem leitura por uns segundos
        r.Add(new StressSample(start.AddSeconds(s), gap ? null : (float)cpuT, 100, (float)(86 + rnd.NextDouble() * 4),
                               fail ? null : (float)gpuT, fail ? null : 112, fail ? null : 140 + rnd.Next(10), s / 600, 0));
        if (s > 0 && s % 600 == 0) r.AddLog(start.AddSeconds(s), $"RAM: ciclo {s / 600} completo, 0 erros · 31,8 GB/s");
    }
    r.CpuMaxTemp = (float)r.Samples.Max(x => x.CpuTemp ?? 0);
    r.GpuMaxTemp = (float)r.Samples.Max(x => x.GpuTemp ?? 0);
    r.GpuFrames = fail ? 0 : 1_260_441; r.GpuAvgFps = fail ? 0 : 144.2;
    r.RamResult = RamRun(fail);
    if (fail)
    {
        r.AddLog(start.AddSeconds(seconds), "RAM: 2 erros em \"Aleatório\" (costuma indicar: erros que dependem dos dados (timings no limite))");
        r.AddLog(start.AddSeconds(seconds), "RAM: thread 1, posição +0x2000000: esperado 9E3779B97F4A7C15, lido 9E3779B97F4A7C95 (1 bit(s))");
        r.Finish($"ABORTADO: {r.RamResult.Errors} erros de memória detectados (primeiro no padrão \"Aleatório\", 1 bit(s) trocado(s))", start.AddSeconds(seconds));
    }
    else r.Finish("Concluído (tempo esgotado)", start.AddSeconds(seconds));
    r.AddLog(r.End!.Value, r.Reason! + " · duração " + (r.End - r.Start)!.Value.ToString(@"hh\:mm\:ss"));
    return r;
}

foreach (var (name, failCase, secs) in new[] { ("aprovado", false, 1800), ("reprovado", true, 420) })
{
    var r = Build(failCase, secs);
    string pdf = Path.Combine(outDir, $"stress_{name}.pdf"), csv = Path.Combine(outDir, $"stress_{name}.csv");
    r.SavePdf(pdf); r.SaveCsv(csv);
    var bytes = File.ReadAllBytes(pdf);
    string head = Encoding.Latin1.GetString(bytes, 0, 8), tail = Encoding.Latin1.GetString(bytes, bytes.Length - 6, 6);
    Check(head.StartsWith("%PDF-1.4") && tail.Contains("%%EOF"), $"{name}: PDF bem formado ({bytes.Length / 1024} KB)");
    var lines = File.ReadAllLines(csv, Encoding.UTF8);
    Check(lines.Length == secs + 1 && lines[0].StartsWith("Data;Hora;"), $"{name}: CSV com {lines.Length - 1} linhas de amostra");
    Check(lines[1].Contains(",") && lines[1].Split(';').Length == 11, $"{name}: CSV em formato pt-BR (; e vírgula decimal)");
    Check(failCase ? r.Verdict().Level == StatusLevel.Bad : r.Verdict().Level == StatusLevel.Good, $"{name}: veredito {r.Verdict().Text}");
}
Check(SimplePdf.Wrap("palavra " + new string('x', 400), 10, SimplePdf.Font.Regular, 200).All(l => SimplePdf.Measure(l, 10, SimplePdf.Font.Regular) <= 200.01),
      "quebra de linha nunca passa da largura");
Console.WriteLine($"\nArquivos em {outDir}");
return fail == 0 ? 0 : 1;
