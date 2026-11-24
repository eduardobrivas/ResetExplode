using System.IO;
using System.Globalization;
using System.Text;

namespace ResetExplode;

/// <summary>Uma amostra por segundo durante o stress test.</summary>
public sealed record StressSample(DateTime At, float? CpuTemp, float? CpuLoad, float? CpuPower,
                                  float? GpuTemp, float? GpuPower, double? Fps, long RamPasses, long RamErrors);

/// <summary>Resultado do teste de RAM copiado no fim (o MemTester libera a memória ao parar).</summary>
public sealed record RamResult(double TestedGB, double InstalledGB, int Threads, int LockedRegions, long Passes, long Errors,
                               double ThroughputGBs, IReadOnlyList<(string Test, string Detects, long Errors)> ByTest,
                               IReadOnlyList<MemError> FirstErrors);

/// <summary>
/// Tudo de uma sessão do stress test: configuração, máquina, amostras, registro e resultado.
/// Exporta CSV (amostras, para Excel) e PDF (relatório). Não depende da janela, para dar para testar.
/// </summary>
public sealed class StressReport
{
    public DateTime Start { get; init; } = DateTime.Now;
    public DateTime? End { get; private set; }
    public string? Reason { get; private set; }

    // Configuração
    public bool Cpu { get; init; }
    public bool Ram { get; init; }
    public bool Gpu { get; init; }
    public double CpuLimit { get; init; }
    public double GpuLimit { get; init; }
    public double RamFraction { get; init; }
    public double DurationMinutes { get; init; }
    public string GpuSettings { get; set; } = ""; // definido depois que a janela da GPU abre

    // Máquina
    public string CpuName { get; init; } = "";
    public string GpuName { get; init; } = "";
    public double InstalledRamGB { get; init; }
    public string Os { get; init; } = "";
    public string AppVersion { get; init; } = "";

    // Resultado
    public float CpuMaxTemp { get; set; }
    public float GpuMaxTemp { get; set; }
    public long GpuFrames { get; set; }
    public double GpuAvgFps { get; set; }
    public RamResult? RamResult { get; set; }

    private readonly List<StressSample> _samples = new();
    private readonly List<(DateTime At, string Msg)> _log = new();
    public IReadOnlyList<StressSample> Samples => _samples;
    public IReadOnlyList<(DateTime At, string Msg)> Log => _log;

    public void Add(StressSample s) => _samples.Add(s);
    public void AddLog(DateTime at, string msg) => _log.Add((at, msg));

    public void Finish(string reason, DateTime? at = null)
    {
        Reason = reason;
        End = at ?? DateTime.Now;
    }

    public bool Aborted => Reason?.StartsWith("ABORTADO") == true;
    public TimeSpan Duration => (End ?? DateTime.Now) - Start;

    /// <summary>Veredito curto para o topo do relatório.</summary>
    public (string Text, StatusLevel Level) Verdict()
    {
        if (End == null) return ("EM ANDAMENTO", StatusLevel.Warn);
        if (RamResult is { Errors: > 0 }) return ("REPROVADO · erros de memória", StatusLevel.Bad);
        if (Aborted) return ("ABORTADO", StatusLevel.Bad);
        if (Reason?.StartsWith("Concluído") == true) return ("CONCLUÍDO SEM ERROS", StatusLevel.Good);
        return ("PARADO PELO USUÁRIO · sem erros até a parada", StatusLevel.Warn);
    }

    // ------------------------------------------------------------------ CSV

    /// <summary>
    /// CSV no formato do Excel em português: separador ";" e vírgula decimal, UTF-8 com BOM (acentos corretos).
    /// Uma linha por segundo; célula vazia = sensor sem leitura.
    /// </summary>
    public string ToCsv(CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.GetCultureInfo("pt-BR");
        string F(double? v, string fmt) => v is double d ? d.ToString(fmt, c) : "";
        var sb = new StringBuilder();
        sb.AppendLine("Data;Hora;Segundos;CPU temp (°C);CPU carga (%);CPU consumo (W);GPU temp (°C);GPU consumo (W);GPU FPS;RAM ciclos;RAM erros");
        foreach (var s in _samples)
            sb.AppendLine(string.Join(";",
                s.At.ToString("yyyy-MM-dd", c), s.At.ToString("HH:mm:ss", c),
                ((long)(s.At - Start).TotalSeconds).ToString(c),
                F(s.CpuTemp, "0.0"), F(s.CpuLoad, "0"), F(s.CpuPower, "0.0"),
                F(s.GpuTemp, "0.0"), F(s.GpuPower, "0.0"), F(s.Fps, "0"),
                s.RamPasses.ToString(c), s.RamErrors.ToString(c)));
        return sb.ToString();
    }

    public void SaveCsv(string path) => File.WriteAllText(path, ToCsv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    // ------------------------------------------------------------------ PDF

    public byte[] ToPdf()
    {
        var pdf = new SimplePdf($"ResetExplode {AppVersion} · relatório gerado em {DateTime.Now:dd/MM/yyyy HH:mm}");
        var pt = CultureInfo.GetCultureInfo("pt-BR");

        // Título
        pdf.TextAt(SimplePdf.Margin, pdf.Y + 18, "Relatório de stress test", 18, SimplePdf.Font.Bold);
        pdf.Space(26);
        pdf.Paragraph($"{Start:dd/MM/yyyy HH:mm:ss} · duração {Duration:hh\\:mm\\:ss}", 10, color: SimplePdf.Gray);
        pdf.Space(8);

        // Veredito
        var (vText, vLevel) = Verdict();
        var (fg, tint) = vLevel switch
        {
            StatusLevel.Good => (SimplePdf.Good, SimplePdf.TintGood),
            StatusLevel.Bad => (SimplePdf.Bad, SimplePdf.TintBad),
            _ => (SimplePdf.Warn, SimplePdf.TintWarn),
        };
        var reasonLines = SimplePdf.Wrap(Reason ?? "Teste ainda rodando no momento da exportação.", 10, SimplePdf.Font.Regular, pdf.ContentW - 24);
        double boxH = 30 + reasonLines.Count * 13.5;
        pdf.Need(boxH + 8);
        pdf.Rect(SimplePdf.Margin, pdf.Y, pdf.ContentW, boxH, tint);
        pdf.Rect(SimplePdf.Margin, pdf.Y, 4, boxH, fg);
        pdf.TextAt(SimplePdf.Margin + 14, pdf.Y + 19, vText, 13, SimplePdf.Font.Bold, fg);
        double ry = pdf.Y + 36;
        foreach (var l in reasonLines) { pdf.TextAt(SimplePdf.Margin + 14, ry, l, 10); ry += 13.5; }
        pdf.Space(boxH + 6);

        // Máquina e configuração
        pdf.Heading("Máquina e configuração");
        pdf.Table(new[] { "Item", "Valor" }, new[]
        {
            new[] { "CPU", CpuName },
            new[] { "GPU", GpuName },
            new[] { "RAM instalada", $"{InstalledRamGB.ToString("0.0", pt)} GB" },
            new[] { "Sistema", Os },
            new[] { "Cargas", string.Join(" + ", new[] { Cpu ? "CPU" : null, Ram ? "RAM" : null, Gpu ? "GPU" : null }.Where(x => x != null)) },
            new[] { "Limite de temperatura", $"CPU {CpuLimit:0} °C" + (Gpu ? $" · GPU {GpuLimit:0} °C" : "") },
            new[] { "Duração programada", DurationMinutes > 0 ? $"{DurationMinutes.ToString("0.##", pt)} min" : "sem limite" },
            new[] { "RAM em teste", Ram ? $"{RamFraction:P0} da memória livre" : "—" },
            new[] { "GPU", Gpu ? GpuSettings : "—" },
        }, new[] { 1.0, 3.0 });

        // Resultados principais
        pdf.Heading("Resultado");
        var rows = new List<string[]>();
        var levels = new List<StatusLevel>();
        void R(string a, string b, StatusLevel l = StatusLevel.Normal) { rows.Add(new[] { a, b }); levels.Add(l); }
        if (Cpu || Ram) R("CPU temperatura máxima", CpuMaxTemp > 0 ? $"{CpuMaxTemp.ToString("0.0", pt)} °C (limite {CpuLimit:0} °C)" : "sem leitura",
                          StatusColors.Temperature(CpuMaxTemp > 0 ? CpuMaxTemp : null, CpuLimit));
        var cpuPower = _samples.Where(s => s.CpuPower is > 0).Select(s => (double)s.CpuPower!.Value).ToList();
        if (cpuPower.Count > 0) R("CPU consumo", $"média {cpuPower.Average().ToString("0", pt)} W · máximo {cpuPower.Max().ToString("0", pt)} W");
        if (Gpu)
        {
            R("GPU temperatura máxima", GpuMaxTemp > 0 ? $"{GpuMaxTemp.ToString("0.0", pt)} °C (limite {GpuLimit:0} °C)" : "sem leitura",
              StatusColors.Temperature(GpuMaxTemp > 0 ? GpuMaxTemp : null, GpuLimit));
            R("GPU", $"{GpuFrames.ToString("N0", pt)} frames · média {GpuAvgFps.ToString("0", pt)} FPS");
        }
        if (RamResult is { } rr)
        {
            R("RAM testada", $"{rr.TestedGB.ToString("0.0", pt)} GB de {rr.InstalledGB.ToString("0.0", pt)} GB " +
                             $"({(rr.InstalledGB > 0 ? rr.TestedGB / rr.InstalledGB : 0):P0}) em {rr.Threads} threads");
            R("Memória travada na RAM", rr.LockedRegions == rr.Threads ? "sim" : rr.LockedRegions == 0 ? "não" : $"parcial ({rr.LockedRegions} de {rr.Threads})",
              rr.LockedRegions == rr.Threads ? StatusLevel.Normal : StatusLevel.Warn);
            R("Ciclos completos", rr.Passes.ToString(pt), rr.Errors == 0 && rr.Passes > 0 ? StatusLevel.Good : StatusLevel.Normal);
            R("Erros de memória", rr.Errors.ToString("N0", pt), rr.Errors > 0 ? StatusLevel.Bad : StatusLevel.Good);
            R("Banda média do teste", $"{rr.ThroughputGBs.ToString("0.0", pt)} GB/s");
        }
        pdf.Table(new[] { "Medida", "Valor" }, rows, new[] { 1.4, 2.6 },
                  cellColor: (r, c) => c == 1 ? ColorOf(levels[r]) : null);

        // Gráfico de temperatura
        if (_samples.Count >= 2) TemperatureChart(pdf);

        // RAM por padrão
        if (RamResult is { } ram)
        {
            pdf.Heading("Teste de RAM por padrão");
            pdf.Table(new[] { "Padrão", "O que costuma indicar", "Erros" },
                      ram.ByTest.Select(t => new[] { t.Test, t.Detects, t.Errors.ToString("N0", pt) }),
                      new[] { 1.2, 2.6, 0.6 }, cellColor: (r, c) => c == 2 && ram.ByTest[r].Errors > 0 ? SimplePdf.Bad : null);
            if (ram.FirstErrors.Count > 0)
            {
                pdf.Paragraph("Primeiros erros (endereço relativo ao bloco da thread):", 9.5, SimplePdf.Font.Bold);
                pdf.Space(2);
                foreach (var e in ram.FirstErrors.Take(12))
                    pdf.Paragraph($"{e.Test,-17} thread {e.Thread,2}  +0x{e.OffsetBytes:X9}  esperado {e.Expected:X16}  lido {e.Actual:X16}  {e.BitsFlipped} bit(s)",
                                  7.5, SimplePdf.Font.Mono);
                pdf.Space(4);
                pdf.Paragraph("Erro com a memória no padrão (sem XMP/OC) indica defeito no pente. Com XMP ou timings manuais: afrouxe " +
                              "timings, suba a tensão da DRAM com cuidado ou reduza a frequência. Erros concentrados numa thread " +
                              "(um núcleo) apontam para instabilidade da CPU, não da RAM.", 9, color: SimplePdf.Gray);
            }
        }

        // Registro
        pdf.Heading("Registro");
        foreach (var (at, msg) in _log)
        {
            var lvl = StatusColors.LogLine(msg);
            pdf.Paragraph($"{at:HH:mm:ss}  {msg}", 8, SimplePdf.Font.Mono, lvl == StatusLevel.Normal ? SimplePdf.Black : ColorOf(lvl));
        }

        return pdf.ToBytes();
    }

    public void SavePdf(string path) => File.WriteAllBytes(path, ToPdf());

    private static SimplePdf.Rgb? ColorOf(StatusLevel l) => l switch
    {
        StatusLevel.Good => SimplePdf.Good,
        StatusLevel.Warn => SimplePdf.Warn,
        StatusLevel.Bad => SimplePdf.Bad,
        _ => null,
    };

    /// <summary>Temperatura da CPU (preto) e da GPU (cinza) ao longo do teste, com o limite tracejado da CPU.</summary>
    private void TemperatureChart(SimplePdf pdf)
    {
        double h = 150, x0 = SimplePdf.Margin + 30, w = pdf.ContentW - 30;
        pdf.Need(h + 34 + 40); // título e gráfico na mesma página
        pdf.Heading("Temperatura durante o teste");
        double top = pdf.Y + 4;
        var temps = _samples.SelectMany(s => new[] { s.CpuTemp, s.GpuTemp }).Where(t => t is > 0).Select(t => (double)t!.Value).ToList();
        double lo = Math.Floor(Math.Min(temps.DefaultIfEmpty(30).Min(), 30) / 10) * 10;
        double hi = Math.Ceiling(Math.Max(temps.DefaultIfEmpty(90).Max(), Math.Max(CpuLimit, Gpu ? GpuLimit : 0)) / 10) * 10 + 5;
        double total = Math.Max(1, (_samples[^1].At - _samples[0].At).TotalSeconds);
        double X(DateTime t) => x0 + (t - _samples[0].At).TotalSeconds / total * w;
        double Yv(double v) => top + h - (v - lo) / (hi - lo) * h;

        for (double v = lo; v <= hi; v += 10)
        {
            pdf.Line(x0, Yv(v), x0 + w, Yv(v), SimplePdf.Rule, 0.4);
            pdf.TextAt(SimplePdf.Margin, Yv(v) + 3, $"{v:0} °C", 7, color: SimplePdf.Gray);
        }
        // Limite da CPU: segmentos curtos (tracejado manual)
        for (double x = x0; x < x0 + w; x += 8) pdf.Line(x, Yv(CpuLimit), Math.Min(x + 4, x0 + w), Yv(CpuLimit), SimplePdf.Bad, 0.8);

        void Series(Func<StressSample, float?> f, SimplePdf.Rgb color)
        {
            var pts = new List<(double, double)>();
            foreach (var s in _samples)
            {
                if (f(s) is float v && v > 0) pts.Add((X(s.At), Yv(v)));
                else if (pts.Count > 0) { pdf.Polyline(pts, color, 1.1); pts.Clear(); } // lacuna sem leitura
            }
            pdf.Polyline(pts, color, 1.1);
        }
        Series(s => s.CpuTemp, SimplePdf.Black);
        if (Gpu) Series(s => s.GpuTemp, SimplePdf.Gray);

        double ly = top + h + 16;
        pdf.TextAt(x0, ly, $"{_samples[0].At:HH:mm}", 7, color: SimplePdf.Gray);
        string endT = $"{_samples[^1].At:HH:mm}";
        pdf.TextAt(x0 + w - SimplePdf.Measure(endT, 7, SimplePdf.Font.Regular), ly, endT, 7, color: SimplePdf.Gray);
        pdf.Line(x0 + 120, ly - 3, x0 + 136, ly - 3, SimplePdf.Black, 1.1);
        pdf.TextAt(x0 + 140, ly, "CPU", 7.5);
        if (Gpu) { pdf.Line(x0 + 170, ly - 3, x0 + 186, ly - 3, SimplePdf.Gray, 1.1); pdf.TextAt(x0 + 190, ly, "GPU", 7.5); }
        pdf.Line(x0 + 222, ly - 3, x0 + 238, ly - 3, SimplePdf.Bad, 0.8);
        pdf.TextAt(x0 + 242, ly, $"limite da CPU ({CpuLimit:0} °C)", 7.5);
        pdf.Space(h + 30);
    }
}
