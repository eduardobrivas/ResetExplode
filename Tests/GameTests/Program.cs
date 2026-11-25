using System.Globalization;
using System.Text;
using ResetExplode;

int ok = 0, fail = 0;
void Run(string name, Action body)
{
    try { body(); ok++; Console.WriteLine($"  OK    {name}"); }
    catch (Exception ex) { fail++; Console.WriteLine($"  FALHOU {name}\n        {ex.Message}"); }
}
void Near(double exp, double got, double tol, string what) { if (Math.Abs(exp - got) > tol) throw new Exception($"{what}: esperado {exp:0.###}, veio {got:0.###}"); }
void True(bool c, string what) { if (!c) throw new Exception(what); }

double[] Steady(int n, double ft) { var t = new double[n]; for (int i = 1; i < n; i++) t[i] = t[i - 1] + ft; return t; }

Run("FPS constante: médio = 1% low = 0,1% low, nenhum engasgo", () =>
{
    var s = GameSession.Analyze(Steady(9001, 1000 / 150.0)); // 60 s a 150 FPS
    Near(150, s.AvgFps, 0.01, "FPS médio"); Near(150, s.Low1Fps, 0.01, "1% low"); Near(150, s.Low01Fps, 0.01, "0,1% low");
    True(s.Stutters == 0, $"engasgos: {s.Stutters}"); Near(60, s.PlayedSeconds, 0.01, "tempo jogado");
});

Run("Picos conhecidos: 1% low e engasgos batem com a conta à mão", () =>
{
    // 20.000 frames a 6,667 ms, 1 a cada 1.000 vira 40 ms → 20 picos. Pior 1% = 200 frames: 20 de 40 ms + 180 de 6,667 ms.
    int n = 20_000; double ft = 1000 / 150.0;
    var t = new double[n + 1];
    for (int i = 1; i <= n; i++) t[i] = t[i - 1] + (i % 1000 == 500 ? 40 : ft);
    var s = GameSession.Analyze(t);
    double worstMean = (20 * 40 + 180 * ft) / 200;
    Near(1000 / worstMean, s.Low1Fps, 0.01, "1% low");
    Near(25, s.Low01Fps, 0.01, "0,1% low (20 piores = os 20 picos de 40 ms)");
    True(s.Stutters == 20, $"engasgos: {s.Stutters} (esperado 20)");
    Near(40, s.WorstFrames[0].Ms, 1e-9, "pior frame");
    Near(n / (n * ft / 1000 + 20 * (40 - ft) / 1000), s.AvgFps, 0.01, "FPS médio");
});

Run("Variação normal (±20%) não conta como engasgo", () =>
{
    var rnd = new Random(7); var t = new double[30_001];
    for (int i = 1; i < t.Length; i++) t[i] = t[i - 1] + 6.667 * (0.8 + 0.4 * rnd.NextDouble());
    True(GameSession.Analyze(t).Stutters == 0, "contou engasgo em variação normal");
});

Run("Pausa de 5 s (alt-tab) fica fora das contas", () =>
{
    var a = Steady(4501, 1000 / 150.0); var t = a.Concat(Steady(4501, 1000 / 150.0).Skip(1).Select(x => x + a[^1] + 5000)).ToArray();
    var s = GameSession.Analyze(t);
    True(s.Gaps == 1, $"pausas: {s.Gaps}"); Near(150, s.AvgFps, 0.01, "FPS médio sem a pausa"); Near(150, s.Low1Fps, 0.01, "1% low sem a pausa");
    Near(60, s.PlayedSeconds, 0.01, "tempo jogado sem a pausa");
});

Run("Jogo principal = processo que mais ficou na tela com FPS (navegador ignorado)", () =>
{
    var g = new GameSession { Start = new DateTime(2026, 11, 24, 22, 0, 0) };
    for (int i = 0; i < 100; i++)
        g.Add(new GameSample(g.Start.AddSeconds(i), i < 10 ? 200 : 100, i < 10 ? "chrome" : "TslGame", 150, 120, 6.7,
                             70, 40, 80, 80, 4500, 60, 75, 60, 100, 2700, 5, 60, 55, "Gargalo de CPU"));
    for (int i = 0; i < 1000; i++) { g.OnFrame(100, i * 6.667); if (i % 10 == 0) g.OnFrame(200, i * 6.667); }
    True(g.MainGame()?.Name == "TslGame", $"jogo: {g.MainGame()?.Name}");
    True(g.GameFrames().Length == 1000, $"frames do jogo: {g.GameFrames().Length}");
    g.Stop(g.Start.AddSeconds(100));
    g.OnFrame(100, 99999);
    True(g.GameFrames().Length == 1000, "frame aceito depois de parar");
    var csv = g.SamplesCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    True(csv.Length == 101 && csv[1].Split(';').Length == 21, $"CSV por segundo: {csv.Length} linhas, {csv[1].Split(';').Length} colunas");
    True(g.FramesCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 1000, "CSV de frames: 999 frametimes + cabeçalho");
});

string? logPath = args.ElementAtOrDefault(0);
string outDir = args.ElementAtOrDefault(1) ?? Path.Combine(Path.GetTempPath(), "sessoes");
if (logPath != null && File.Exists(logPath))
    Run($"Relatório de exemplo a partir do log real ({Path.GetFileName(logPath)})", () =>
    {
        var g = BuildFromHwinfo(logPath);
        var dir = g.SaveAll(outDir);
        var files = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Console.WriteLine($"      {g.Samples.Count} s gravados, {g.FrameCount:N0} frames → {dir}");
        var s = GameSession.Analyze(g.GameFrames());
        Console.WriteLine($"      FPS médio {s.AvgFps:0.0} · 1% low {s.Low1Fps:0.0} · 0,1% low {s.Low01Fps:0.0} · engasgos {s.Stutters}");
        True(files.SequenceEqual(new[] { "frametimes.csv", "relatorio.pdf", "sessao.csv" }), string.Join(",", files));
        True(Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(dir, "relatorio.pdf")), 0, 8).StartsWith("%PDF"), "PDF");
    });

Console.WriteLine($"\n{ok} passaram, {fail} falharam");
return fail == 0 ? 0 : 1;

// Monta uma sessão "como se" o app tivesse gravado o PUBG do log: sensores a cada 2 s viram 2 amostras de 1 s,
// e os frames são gerados com o FPS de cada amostra (variação de ±15% e um engasgo de vez em quando).
static GameSession BuildFromHwinfo(string path)
{
    var lines = File.ReadAllLines(path, Encoding.UTF8);
    var head = Split(lines[0]);
    int Col(string n) { int i = Array.IndexOf(head, n); if (i < 0) throw new Exception("coluna " + n); return i; }
    int cFps = Col("Taxa de quadros Displayed (avg) [FPS]"), cLow = Col("Taxa de quadros Displayed (1% low) [FPS]"),
        cCt = Col("CPU (Tctl/Tdie) [°C]"), cCl = Col("Uso total da CPU [%]"), cMax = Col("Uso máximo de CPU / thread [%]"),
        cPw = Col("CPU PPT [W]"), cClk = Col("Relógio efetivo médio [MHz]"), cGt = Col("Temperatura GPU [°C]"),
        cHs = Col("Temperatura de ponto quente da GPU [°C]"), cGl = Col("Carga do núcleo da GPU [%]"), cGp = Col("GPU Potência (Total) [W]"),
        cGc = Col("GPU Clock [MHz]"), cVr = Col("Memória GPU alocada [MB]"), cRam = Col("Carga da memória física [%]");
    float? F(string[] f, int i) => float.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    var start = new DateTime(2026, 11, 21, 21, 0, 0);
    var g = new GameSession { Start = start, CpuName = "AMD Ryzen 7 5700X", GpuName = "NVIDIA GeForce RTX 4060", AppVersion = "exemplo" };
    var rnd = new Random(3);
    double tMs = 0; int sec = 0;
    foreach (var line in lines.Skip(1))
    {
        var f = line.Split(',');
        if (f.Length < head.Length - 1 || !char.IsDigit(f[1].FirstOrDefault())) continue;
        double? fps = F(f, cFps); bool inGame = fps > 30 && F(f, cGl) > 40;
        for (int k = 0; k < 2; k++, sec++)
        {
            var at = start.AddSeconds(sec);
            float? gl = F(f, cGl), mx = F(f, cMax);
            string lim = !inGame ? "" : gl >= 90 ? "GPU no limite" : mx >= 85 ? "Gargalo de CPU" : "Nada no limite";
            g.Add(new GameSample(at, inGame ? 4242 : 77, inGame ? "TslGame" : "explorer", inGame ? fps : null, inGame ? F(f, cLow) : null,
                                 inGame ? 1000 / fps : null, F(f, cCt), F(f, cCl), mx, F(f, cPw), F(f, cClk), F(f, cGt), F(f, cHs), gl,
                                 F(f, cGp), (uint?)F(f, cGc), F(f, cVr) / 1024.0, F(f, cVr) / 8188.0 * 100, F(f, cRam), inGame ? lim : null));
            if (!inGame) { tMs += 1000; continue; }
            double end = tMs + 1000, mean = 1000 / fps!.Value;
            while (tMs < end)
            {
                double ft = mean * (0.85 + 0.3 * rnd.NextDouble());
                if (rnd.NextDouble() < 0.0006) ft = 25 + rnd.NextDouble() * 40; // engasgo
                tMs += ft;
                g.OnFrame(4242, tMs);
            }
        }
    }
    g.Stop(start.AddSeconds(sec));
    return g;
}

static string[] Split(string line)
{
    var res = new List<string>(); var cur = new StringBuilder(); bool q = false;
    foreach (char ch in line.TrimStart('﻿'))
    {
        if (ch == '"') q = !q;
        else if (ch == ',' && !q) { res.Add(cur.ToString()); cur.Clear(); }
        else cur.Append(ch);
    }
    res.Add(cur.ToString());
    return res.ToArray();
}
