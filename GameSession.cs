using System.Globalization;
using System.IO;
using System.Text;

namespace ResetExplode;

/// <summary>Uma leitura por segundo durante a gravação de uma sessão de jogo.</summary>
public sealed record GameSample(
    DateTime At, int? Pid, string? Game, double? Fps, double? Low1Fps, double? FrameTimeMs,
    float? CpuTemp, float? CpuLoad, float? CpuMaxCore, float? CpuPower, float? CpuClockAvg,
    float? GpuTemp, float? GpuHotSpot, float? GpuLoad, float? GpuPower, uint? GpuClock,
    double? VramUsedGB, double? VramPct, float? RamPct, string? Limiter);

/// <summary>Resumo calculado a partir de TODOS os frames do jogo (não de médias por janela).</summary>
public sealed record FrameSummary(
    int Frames, double PlayedSeconds, double AvgFps, double Low1Fps, double Low01Fps, double P99Ms, double P999Ms,
    double MedianMs, int Stutters, int Gaps, IReadOnlyList<(double AtSec, double Ms)> WorstFrames);

/// <summary>
/// Grava uma sessão de jogo: os sensores uma vez por segundo e o instante de cada frame (evento Present via ETW)
/// de todos os processos; no fim, fica com os frames do jogo (o processo que mais ficou em primeiro plano com FPS).
/// Com todos os frames dá para calcular o 1% low real da sessão inteira, o que não sai de médias por janela.
/// Não depende da janela, para dar para testar.
/// </summary>
public sealed class GameSession
{
    /// <summary>Limite de frames guardados (5 milhões ≈ 40 MB ≈ 4,5 h a 300 FPS). Passou disso, para de guardar frames.</summary>
    public const int MaxFrames = 5_000_000;

    /// <summary>Intervalo entre frames acima disso = jogo pausado, minimizado ou em loading: fica fora das contas.</summary>
    public const double GapMs = 1000;

    public DateTime Start { get; init; } = DateTime.Now;
    public DateTime? End { get; private set; }
    public string CpuName { get; init; } = "";
    public string GpuName { get; init; } = "";
    public string AppVersion { get; init; } = "";

    private readonly List<GameSample> _samples = new();
    private readonly Dictionary<int, List<double>> _frames = new(); // pid → instantes (ms, relógio do ETW)
    private readonly object _lock = new();
    private int _frameCount;
    private bool _truncated;

    public IReadOnlyList<GameSample> Samples => _samples;
    public int FrameCount => Volatile.Read(ref _frameCount);
    public bool Truncated => _truncated;
    public bool Recording => End == null;
    public TimeSpan Duration => (End ?? DateTime.Now) - Start;

    /// <summary>Chamado pela thread do ETW a cada frame de qualquer processo.</summary>
    public void OnFrame(int pid, double tMs)
    {
        if (End != null) return;
        lock (_lock)
        {
            if (_frameCount >= MaxFrames) { _truncated = true; return; }
            if (!_frames.TryGetValue(pid, out var l)) _frames[pid] = l = new List<double>(65536);
            l.Add(tMs);
            _frameCount++;
        }
    }

    public void Add(GameSample s) { if (End == null) _samples.Add(s); }

    public void Stop(DateTime? at = null) => End ??= at ?? DateTime.Now;

    /// <summary>O jogo da sessão: o processo que mais segundos ficou em primeiro plano mostrando FPS.</summary>
    public (int Pid, string Name)? MainGame()
    {
        var g = _samples.Where(s => s.Pid != null && s.Fps > 0)
                        .GroupBy(s => s.Pid!.Value)
                        .OrderByDescending(x => x.Count())
                        .FirstOrDefault();
        return g == null ? null : (g.Key, g.Last().Game ?? $"PID {g.Key}");
    }

    /// <summary>Instantes dos frames do jogo principal (cópia).</summary>
    public double[] GameFrames()
    {
        if (MainGame() is not { } game) return Array.Empty<double>();
        lock (_lock) return _frames.TryGetValue(game.Pid, out var l) ? l.ToArray() : Array.Empty<double>();
    }

    // ------------------------------------------------------------------ análise

    /// <summary>
    /// Contas sobre os frames (instantes em ms, em ordem):
    /// FPS médio = frames ÷ tempo jogado; 1% low = 1000 ÷ média do pior 1% dos frametimes (definição do CapFrameX
    /// e do FrameView); 0,1% low igual com o pior 0,1%; P99/P99,9 = percentis do frametime.
    /// Engasgo = frame ≥ 2,5× a média dos 60 anteriores e pelo menos 8 ms mais lento que ela.
    /// Intervalos acima de 1 s (pausa, alt-tab, loading) ficam fora de tudo.
    /// </summary>
    public static FrameSummary Analyze(IReadOnlyList<double> t)
    {
        var ft = new List<double>(Math.Max(0, t.Count - 1));
        var at = new List<double>(ft.Capacity);
        int gaps = 0;
        for (int i = 1; i < t.Count; i++)
        {
            double d = t[i] - t[i - 1];
            if (d <= 0) continue;
            if (d > GapMs) { gaps++; continue; }
            ft.Add(d);
            at.Add(t[i]);
        }
        if (ft.Count < 10) return new FrameSummary(ft.Count, ft.Sum() / 1000, 0, 0, 0, 0, 0, 0, 0, gaps, Array.Empty<(double, double)>());

        double played = ft.Sum();
        var sorted = ft.ToArray();
        Array.Sort(sorted);
        double Pct(double p) => sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(sorted.Length * p))];
        double WorstMean(double frac)
        {
            int n = Math.Max(1, (int)Math.Ceiling(sorted.Length * frac));
            double s = 0;
            for (int i = sorted.Length - n; i < sorted.Length; i++) s += sorted[i];
            return s / n;
        }

        // Engasgos com média móvel dos 60 frames anteriores (barato e estável).
        int stutters = 0;
        double win = 0;
        var q = new Queue<double>(61);
        for (int i = 0; i < ft.Count; i++)
        {
            if (q.Count == 60)
            {
                double mean = win / 60;
                if (ft[i] >= 2.5 * mean && ft[i] - mean >= 8) stutters++;
            }
            q.Enqueue(ft[i]); win += ft[i];
            if (q.Count > 60) win -= q.Dequeue();
        }

        double t0 = t[0];
        var worst = ft.Select((d, i) => (AtSec: (at[i] - t0) / 1000, Ms: d)).OrderByDescending(x => x.Ms).Take(10).ToList();
        return new FrameSummary(ft.Count, played / 1000, ft.Count / (played / 1000), 1000 / WorstMean(0.01), 1000 / WorstMean(0.001),
                                Pct(0.99), Pct(0.999), Pct(0.5), stutters, gaps, worst);
    }

    /// <summary>Quanto do tempo jogado ficou em cada classificação do analisador de gargalo.</summary>
    public IReadOnlyList<(string Limiter, double Pct)> LimiterShare()
    {
        var withFps = _samples.Where(s => s.Fps > 0 && s.Limiter != null).ToList();
        if (withFps.Count == 0) return Array.Empty<(string, double)>();
        return withFps.GroupBy(s => s.Limiter!).Select(g => (g.Key, 100.0 * g.Count() / withFps.Count))
                      .OrderByDescending(x => x.Item2).ToList();
    }

    // ------------------------------------------------------------------ arquivos

    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Uma linha por segundo, formato do Excel em português (";" e vírgula decimal).</summary>
    public string SamplesCsv()
    {
        string F(double? v, string fmt) => v is double d ? d.ToString(fmt, Pt) : "";
        var sb = new StringBuilder();
        sb.AppendLine("Data;Hora;Segundos;Jogo;FPS;1% low (10 s);Frametime (ms);CPU temp (°C);CPU carga (%);CPU núcleo mais usado (%);" +
                      "CPU consumo (W);CPU clock médio (MHz);GPU temp (°C);GPU hot spot (°C);GPU carga (%);GPU consumo (W);GPU clock (MHz);" +
                      "VRAM usada (GB);VRAM (%);RAM (%);Gargalo");
        foreach (var s in _samples)
            sb.AppendLine(string.Join(";",
                s.At.ToString("yyyy-MM-dd", Pt), s.At.ToString("HH:mm:ss", Pt), ((long)(s.At - Start).TotalSeconds).ToString(Pt),
                (s.Game ?? "").Replace(';', ','), F(s.Fps, "0.0"), F(s.Low1Fps, "0.0"), F(s.FrameTimeMs, "0.00"),
                F(s.CpuTemp, "0.0"), F(s.CpuLoad, "0"), F(s.CpuMaxCore, "0"), F(s.CpuPower, "0.0"), F(s.CpuClockAvg, "0"),
                F(s.GpuTemp, "0.0"), F(s.GpuHotSpot, "0.0"), F(s.GpuLoad, "0"), F(s.GpuPower, "0.0"), F(s.GpuClock, "0"),
                F(s.VramUsedGB, "0.00"), F(s.VramPct, "0"), F(s.RamPct, "0"), s.Limiter ?? ""));
        return sb.ToString();
    }

    /// <summary>Todos os frames do jogo: instante desde o 1º frame e frametime, para CapFrameX/Excel/análise própria.</summary>
    public string FramesCsv()
    {
        var t = GameFrames();
        var sb = new StringBuilder(t.Length * 20);
        sb.AppendLine("Tempo (s);Frametime (ms)");
        for (int i = 1; i < t.Length; i++)
            sb.Append(((t[i] - t[0]) / 1000).ToString("0.0000", Pt)).Append(';').AppendLine((t[i] - t[i - 1]).ToString("0.000", Pt));
        return sb.ToString();
    }

    /// <summary>Nome de pasta seguro: data, hora e jogo.</summary>
    public string FolderName()
    {
        string game = MainGame()?.Name ?? "sem-jogo";
        foreach (char c in Path.GetInvalidFileNameChars()) game = game.Replace(c, '_');
        return $"{Start:yyyy-MM-dd_HHmm}_{game}";
    }

    /// <summary>Salva sessao.csv, frametimes.csv e relatorio.pdf numa pasta nova dentro de <paramref name="root"/>.</summary>
    public string SaveAll(string root)
    {
        var dir = Path.Combine(root, FolderName());
        Directory.CreateDirectory(dir);
        var bom = new UTF8Encoding(true);
        File.WriteAllText(Path.Combine(dir, "sessao.csv"), SamplesCsv(), bom);
        File.WriteAllText(Path.Combine(dir, "frametimes.csv"), FramesCsv(), bom);
        File.WriteAllBytes(Path.Combine(dir, "relatorio.pdf"), ToPdf());
        return dir;
    }

    // ------------------------------------------------------------------ PDF

    public byte[] ToPdf()
    {
        var pdf = new SimplePdf($"ResetExplode {AppVersion} · sessão de jogo · gerado em {DateTime.Now:dd/MM/yyyy HH:mm}");
        var game = MainGame();
        var frames = GameFrames();
        var sum = Analyze(frames);

        pdf.TextAt(SimplePdf.Margin, pdf.Y + 18, $"Sessão de jogo · {game?.Name ?? "nenhum jogo detectado"}", 18, SimplePdf.Font.Bold);
        pdf.Space(26);
        pdf.Paragraph($"{Start:dd/MM/yyyy HH:mm:ss} · gravação de {Duration:hh\\:mm\\:ss} · {(sum.PlayedSeconds / 60).ToString("0.0", Pt)} min de jogo medidos" +
                      $" · {CpuName} + {GpuName}", 10, color: SimplePdf.Gray);
        pdf.Space(6);

        if (game == null || sum.Frames < 10)
        {
            pdf.Paragraph("Nenhum frame de jogo foi registrado. O FPS só é medido com o jogo em primeiro plano, em DirectX 9 a 12 " +
                          "(Vulkan/OpenGL só se apresentarem via DXGI).", 10);
            return pdf.ToBytes();
        }

        // Números principais
        var played = _samples.Where(s => s.Fps > 0).ToList();
        string Avg(Func<GameSample, double?> f, string fmt, string unit)
        {
            var v = played.Select(f).Where(x => x is > 0).Select(x => x!.Value).ToList();
            return v.Count == 0 ? "—" : $"{v.Average().ToString(fmt, Pt)} {unit} (máx. {v.Max().ToString(fmt, Pt)})";
        }
        pdf.Heading("Resumo");
        pdf.Table(new[] { "Medida", "Valor" }, new[]
        {
            new[] { "FPS médio", sum.AvgFps.ToString("0.0", Pt) },
            new[] { "1% low / 0,1% low", $"{sum.Low1Fps.ToString("0.0", Pt)} / {sum.Low01Fps.ToString("0.0", Pt)} FPS (média do pior 1% / 0,1% dos frames)" },
            new[] { "Frametime mediano / P99 / P99,9", $"{sum.MedianMs.ToString("0.00", Pt)} / {sum.P99Ms.ToString("0.00", Pt)} / {sum.P999Ms.ToString("0.00", Pt)} ms" },
            new[] { "Engasgos", $"{sum.Stutters.ToString("N0", Pt)} ({(sum.Stutters / Math.Max(1, sum.PlayedSeconds / 60)).ToString("0.0", Pt)} por minuto)" },
            new[] { "Frames medidos", $"{sum.Frames.ToString("N0", Pt)}" + (sum.Gaps > 0 ? $" · {sum.Gaps} pausas acima de 1 s fora das contas" : "") + (Truncated ? " · limite de frames atingido" : "") },
            new[] { "CPU temperatura", Avg(s => s.CpuTemp, "0.0", "°C") },
            new[] { "CPU núcleo mais usado", Avg(s => s.CpuMaxCore, "0", "%") },
            new[] { "CPU consumo", Avg(s => s.CpuPower, "0", "W") },
            new[] { "GPU temperatura", Avg(s => s.GpuTemp, "0.0", "°C") },
            new[] { "GPU carga", Avg(s => s.GpuLoad, "0", "%") },
            new[] { "GPU consumo", Avg(s => s.GpuPower, "0", "W") },
            new[] { "VRAM usada", Avg(s => s.VramUsedGB, "0.0", "GB") },
            new[] { "RAM do sistema", Avg(s => s.RamPct, "0", "%") },
        }, new[] { 1.3, 2.7 });

        var share = LimiterShare();
        if (share.Count > 0)
        {
            pdf.Heading("O que limitou o FPS (segundos com o jogo na tela)");
            pdf.Table(new[] { "Classificação", "Tempo" },
                      share.Select(x => new[] { x.Limiter, $"{x.Pct.ToString("0", Pt)} %" }), new[] { 3.0, 1.0 });
            pdf.Paragraph("Classificação aproximada, pela carga da GPU e do núcleo mais usado da CPU. Jogo limitado pela CPU com a " +
                          "carga espalhada entre vários núcleos (comum em jogos competitivos) pode aparecer como \"Nada no limite\" " +
                          "mesmo sem trava de FPS. Sinal disso: GPU bem abaixo de 90% com o FPS variando junto com a cena.",
                          8.5, color: SimplePdf.Gray);
            pdf.Space(6);
        }

        // Gráficos
        double t0 = frames[0];
        Chart(pdf, "FPS a cada segundo (preto) e 1% low da janela de 10 s (cinza)", played,
              new (Func<GameSample, double?>, SimplePdf.Rgb)[] { (s => s.Fps, SimplePdf.Black), (s => s.Low1Fps, SimplePdf.Gray) }, "FPS");
        FrametimeChart(pdf, frames, sum);
        Chart(pdf, "Temperatura: CPU (preto) e GPU (cinza)", _samples,
              new (Func<GameSample, double?>, SimplePdf.Rgb)[] { (s => s.CpuTemp, SimplePdf.Black), (s => s.GpuTemp, SimplePdf.Gray) }, "°C");
        Chart(pdf, "Carga: GPU (preto) e núcleo mais usado da CPU (cinza)", played,
              new (Func<GameSample, double?>, SimplePdf.Rgb)[] { (s => s.GpuLoad, SimplePdf.Black), (s => s.CpuMaxCore, SimplePdf.Gray) }, "%", 0, 100);

        pdf.Heading("Os 10 piores frames");
        pdf.Table(new[] { "Momento (desde o 1º frame)", "Frametime", "Equivale a" },
                  sum.WorstFrames.Select(w => new[] { TimeSpan.FromSeconds(w.AtSec).ToString(@"hh\:mm\:ss"), $"{w.Ms.ToString("0.0", Pt)} ms",
                                                      $"{(1000 / w.Ms).ToString("0", Pt)} FPS" }),
                  new[] { 1.5, 1.0, 1.0 });

        pdf.Paragraph("Como foi medido: FPS pelos eventos Present do DirectX (DXGI/D3D9) via ETW, sem injetar nada no jogo. " +
                      "São frames apresentados pelo jogo, não necessariamente exibidos na tela. Intervalos acima de 1 s (pausa, " +
                      "alt-tab, loading) ficam fora das contas. Engasgo = frame 2,5× mais lento que a média dos 60 anteriores e " +
                      "pelo menos 8 ms acima dela.", 8.5, color: SimplePdf.Gray);
        return pdf.ToBytes();
    }

    /// <summary>Gráfico de linhas por segundo, eixo X = tempo de gravação.</summary>
    private void Chart(SimplePdf pdf, string title, IReadOnlyList<GameSample> data,
                       (Func<GameSample, double?> F, SimplePdf.Rgb Color)[] series, string unit, double? fixLo = null, double? fixHi = null)
    {
        if (data.Count < 2) return;
        double h = 130, x0 = SimplePdf.Margin + 34, w = pdf.ContentW - 34;
        pdf.Need(h + 70);
        pdf.Heading(title, 11);
        double top = pdf.Y + 4;
        var vals = series.SelectMany(s => data.Select(s.F)).Where(v => v is > 0).Select(v => v!.Value).ToList();
        if (vals.Count == 0) { pdf.Paragraph("Sem leitura.", 9, color: SimplePdf.Gray); return; }
        double lo = fixLo ?? Math.Floor(vals.Min() * 0.95 / 10) * 10, hi = fixHi ?? Math.Ceiling(vals.Max() * 1.05 / 10) * 10;
        if (hi - lo < 10) hi = lo + 10;
        double total = Math.Max(1, Duration.TotalSeconds);
        double X(DateTime t) => x0 + Math.Clamp((t - Start).TotalSeconds / total, 0, 1) * w;
        double Y(double v) => top + h - (Math.Clamp(v, lo, hi) - lo) / (hi - lo) * h;
        double step = NiceStep((hi - lo) / 5);
        for (double v = Math.Ceiling(lo / step) * step; v <= hi + 1e-9; v += step)
        {
            pdf.Line(x0, Y(v), x0 + w, Y(v), SimplePdf.Rule, 0.4);
            pdf.TextAt(SimplePdf.Margin, Y(v) + 3, $"{v.ToString("0", Pt)} {unit}", 7, color: SimplePdf.Gray);
        }
        // Agrupa por coluna do gráfico (média dos segundos que caem nela): 1,5 h = 5.400 s não cabem em 500 pontos.
        int cols = Math.Max(2, (int)(w / 1.5));
        foreach (var (f, color) in series)
        {
            var sum = new double[cols]; var n = new int[cols];
            foreach (var smp in data)
                if (f(smp) is double v && v > 0)
                {
                    int c = (int)Math.Clamp((smp.At - Start).TotalSeconds / total * (cols - 1), 0, cols - 1);
                    sum[c] += v; n[c]++;
                }
            var pts = new List<(double, double)>();
            for (int c = 0; c < cols; c++)
            {
                if (n[c] > 0) pts.Add((x0 + c * w / (cols - 1), Y(sum[c] / n[c])));
                else { pdf.Polyline(pts, color, 0.9); pts.Clear(); } // coluna sem leitura (sem jogo na tela): quebra a linha
            }
            pdf.Polyline(pts, color, 0.9);
        }
        TimeAxis(pdf, x0, w, top + h + 14, total);
        pdf.Space(h + 26);
    }

    /// <summary>Frametime de todos os frames: para cada coluna, a faixa do menor ao maior frametime (picos = engasgos).</summary>
    private void FrametimeChart(SimplePdf pdf, double[] t, FrameSummary sum)
    {
        if (t.Length < 3) return;
        double h = 130, x0 = SimplePdf.Margin + 34, w = pdf.ContentW - 34;
        pdf.Need(h + 70);
        pdf.Heading("Frametime de todos os frames (faixa por coluna; picos = engasgos)", 11);
        double top = pdf.Y + 4;
        double hi = Math.Max(20, Math.Ceiling(Math.Min(sum.P999Ms * 2.5, 200) / 10) * 10), lo = 0;
        double Y(double v) => top + h - (Math.Clamp(v, lo, hi) - lo) / (hi - lo) * h;
        double step = NiceStep(hi / 5);
        for (double v = 0; v <= hi + 1e-9; v += step)
        {
            pdf.Line(x0, Y(v), x0 + w, Y(v), SimplePdf.Rule, 0.4);
            pdf.TextAt(SimplePdf.Margin, Y(v) + 3, $"{v.ToString("0", Pt)} ms", 7, color: SimplePdf.Gray);
        }
        // Eixo X em segundos desde o início da gravação (o 1º frame pode vir depois).
        double total = Math.Max(1, Duration.TotalSeconds);
        var firstPlayed = _samples.FirstOrDefault(s => s.Fps > 0)?.At ?? Start;
        double firstOffset = Math.Max(0, (firstPlayed - Start).TotalSeconds);
        int cols = (int)w;
        var mins = Enumerable.Repeat(double.MaxValue, cols).ToArray();
        var maxs = new double[cols];
        for (int i = 1; i < t.Length; i++)
        {
            double d = t[i] - t[i - 1];
            if (d <= 0 || d > GapMs) continue;
            double sec = firstOffset + (t[i] - t[0]) / 1000;
            int c = (int)Math.Clamp(sec / total * (cols - 1), 0, cols - 1);
            if (d < mins[c]) mins[c] = d;
            if (d > maxs[c]) maxs[c] = d;
        }
        for (int c = 0; c < cols; c++)
            if (maxs[c] > 0) pdf.Line(x0 + c, Y(mins[c]), x0 + c, Y(maxs[c]) - 0.3, SimplePdf.Black, 0.7);
        // Mediana tracejada
        for (double x = x0; x < x0 + w; x += 8) pdf.Line(x, Y(sum.MedianMs), Math.Min(x + 4, x0 + w), Y(sum.MedianMs), SimplePdf.Bad, 0.6);
        TimeAxis(pdf, x0, w, top + h + 14, total);
        pdf.TextAt(x0, top + h + 26, $"Tracejado vermelho = frametime mediano ({sum.MedianMs.ToString("0.0", Pt)} ms)", 7, color: SimplePdf.Gray);
        pdf.Space(h + 36);
    }

    /// <summary>Horário no eixo X: marcas em passos redondos (1 a 60 min), no máximo 8.</summary>
    private void TimeAxis(SimplePdf pdf, double x0, double w, double y, double totalSec)
    {
        int stepMin = new[] { 1, 2, 5, 10, 15, 20, 30, 60 }.FirstOrDefault(m => totalSec / 60 / m <= 8, 120);
        var first = new DateTime(Start.Year, Start.Month, Start.Day, Start.Hour, Start.Minute / stepMin * stepMin, 0).AddMinutes(stepMin);
        double lastRight = -1;
        void Label(DateTime t, bool edge)
        {
            double x = x0 + (t - Start).TotalSeconds / totalSec * w;
            string s = t.ToString("HH:mm");
            double tw = SimplePdf.Measure(s, 7, SimplePdf.Font.Regular);
            double lx = Math.Clamp(x - tw / 2, x0, x0 + w - tw);
            if (lx < lastRight + 6 && !edge) return; // não encavala
            pdf.TextAt(lx, y, s, 7, color: SimplePdf.Gray);
            pdf.Line(x, y - 11, x, y - 9, SimplePdf.Gray, 0.5);
            lastRight = lx + tw;
        }
        Label(Start, true);
        var end = End ?? DateTime.Now;
        for (var t = first; t < end.AddMinutes(-stepMin / 3.0); t = t.AddMinutes(stepMin)) Label(t, false);
    }

    private static double NiceStep(double raw)
    {
        double p = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-9))));
        double f = raw / p;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * p;
    }
}
