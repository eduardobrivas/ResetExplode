using System.Globalization;
using System.Text;

namespace ResetExplode;

/// <summary>
/// Gerador de PDF mínimo, sem bibliotecas: texto (Helvetica, Helvetica-Bold, Courier), cores, linhas, retângulos,
/// tabelas simples e quebra automática de linha e de página. Usa as 14 fontes padrão do PDF (não embute fonte)
/// com codificação WinAnsi, que cobre o português (acentos, ç, °, ·).
/// </summary>
public sealed class SimplePdf
{
    public const double PageW = 595.28, PageH = 841.89; // A4 em pontos
    public const double Margin = 48;
    public double ContentW => PageW - 2 * Margin;

    public enum Font { Regular, Bold, Mono }

    public readonly record struct Rgb(double R, double G, double B)
    {
        public static Rgb Hex(string h) => new(int.Parse(h[1..3], NumberStyles.HexNumber) / 255.0,
                                               int.Parse(h[3..5], NumberStyles.HexNumber) / 255.0,
                                               int.Parse(h[5..7], NumberStyles.HexNumber) / 255.0);
    }

    public static readonly Rgb Black = Rgb.Hex("#111111"), Gray = Rgb.Hex("#5F5F5F"), Rule = Rgb.Hex("#D0D0D0"),
                               Good = Rgb.Hex("#1B7F3B"), Warn = Rgb.Hex("#9A5B00"), Bad = Rgb.Hex("#C4232B"),
                               TintGood = Rgb.Hex("#E6F4EA"), TintWarn = Rgb.Hex("#FFF4E0"), TintBad = Rgb.Hex("#FCE8E8"),
                               TintHead = Rgb.Hex("#F2F2F2");

    private readonly List<StringBuilder> _pages = new();
    private StringBuilder _cur = null!;
    private double _y;
    private readonly string _footer;

    /// <summary>Cursor vertical (de cima para baixo, em pontos a partir do topo da página).</summary>
    public double Y => _y;

    public SimplePdf(string footer)
    {
        _footer = footer;
        NewPage();
    }

    public void NewPage()
    {
        _cur = new StringBuilder();
        _pages.Add(_cur);
        _y = Margin;
    }

    /// <summary>Garante espaço; se não houver, começa outra página.</summary>
    public void Need(double h)
    {
        if (_y + h > PageH - Margin - 18) NewPage();
    }

    public void Space(double h) => _y += h;

    // ------------------------------------------------------------------ primitivas

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static double PdfY(double top) => PageH - top; // PDF mede de baixo para cima

    public void TextAt(double x, double top, string text, double size, Font f = Font.Regular, Rgb? color = null)
    {
        var c = color ?? Black;
        _cur.Append($"BT /{FontName(f)} {N(size)} Tf {N(c.R)} {N(c.G)} {N(c.B)} rg {N(x)} {N(PdfY(top))} Td ({Escape(text)}) Tj ET\n");
    }

    public void Rect(double x, double top, double w, double h, Rgb fill)
        => _cur.Append($"{N(fill.R)} {N(fill.G)} {N(fill.B)} rg {N(x)} {N(PdfY(top + h))} {N(w)} {N(h)} re f\n");

    public void Line(double x1, double top1, double x2, double top2, Rgb color, double width = 0.6)
        => _cur.Append($"{N(color.R)} {N(color.G)} {N(color.B)} RG {N(width)} w {N(x1)} {N(PdfY(top1))} m {N(x2)} {N(PdfY(top2))} l S\n");

    /// <summary>Linha poligonal (gráficos): pontos já em coordenadas da página (x, distância do topo).</summary>
    public void Polyline(IReadOnlyList<(double X, double Top)> pts, Rgb color, double width = 1)
    {
        if (pts.Count < 2) return;
        var sb = new StringBuilder($"{N(color.R)} {N(color.G)} {N(color.B)} RG {N(width)} w 1 j ");
        sb.Append($"{N(pts[0].X)} {N(PdfY(pts[0].Top))} m ");
        for (int i = 1; i < pts.Count; i++) sb.Append($"{N(pts[i].X)} {N(PdfY(pts[i].Top))} l ");
        sb.Append("S\n");
        _cur.Append(sb);
    }

    // ------------------------------------------------------------------ texto com quebra

    /// <summary>Parágrafo com quebra automática na largura dada (padrão: largura útil).</summary>
    public void Paragraph(string text, double size = 10, Font f = Font.Regular, Rgb? color = null, double? width = null, double indent = 0)
    {
        double lh = size * 1.35;
        foreach (var line in Wrap(text, size, f, (width ?? ContentW) - indent))
        {
            Need(lh);
            TextAt(Margin + indent, _y + size, line, size, f, color);
            _y += lh;
        }
    }

    public void Heading(string text, double size = 13)
    {
        Need(size * 2.6);
        _y += size * 0.6;
        TextAt(Margin, _y + size, text, size, Font.Bold);
        _y += size * 1.5;
        Line(Margin, _y, Margin + ContentW, _y, Rule);
        _y += 6;
    }

    /// <summary>Tabela: cabeçalho cinza, bordas finas, células com quebra de linha. widths = frações da largura.</summary>
    public void Table(string[] header, IEnumerable<string[]> rows, double[] widths, double size = 9,
                      Func<int, int, Rgb?>? cellColor = null)
    {
        double total = widths.Sum();
        var w = widths.Select(x => x / total * ContentW).ToArray();
        double pad = 4, lh = size * 1.3;

        void Row(string[] cells, bool head, int r)
        {
            var wrapped = cells.Select((c, i) => Wrap(c ?? "", size, head ? Font.Bold : Font.Regular, w[i] - 2 * pad)).ToArray();
            double h = wrapped.Max(l => Math.Max(1, l.Count)) * lh + 2 * pad;
            if (_y + h > PageH - Margin - 18) { NewPage(); if (!head) Row(header, true, -1); }
            if (head) Rect(Margin, _y, ContentW, h, TintHead);
            double x = Margin;
            for (int i = 0; i < cells.Length; i++)
            {
                var col = head ? Black : cellColor?.Invoke(r, i) ?? Black;
                double ty = _y + pad + size;
                foreach (var l in wrapped[i]) { TextAt(x + pad, ty, l, size, head ? Font.Bold : Font.Regular, col); ty += lh; }
                x += w[i];
            }
            _y += h;
            Line(Margin, _y, Margin + ContentW, _y, Rule, 0.5);
        }

        Need(lh * 3);
        Line(Margin, _y, Margin + ContentW, _y, Rule, 0.5);
        Row(header, true, -1);
        int ri = 0;
        foreach (var r in rows) Row(r, false, ri++);
        _y += 8;
    }

    /// <summary>Quebra o texto em linhas que cabem na largura (por palavras; palavra gigante é cortada).</summary>
    public static List<string> Wrap(string text, double size, Font f, double width)
    {
        var lines = new List<string>();
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var cur = new StringBuilder();
            foreach (var word in para.Split(' '))
            {
                string cand = cur.Length == 0 ? word : cur + " " + word;
                if (Measure(cand, size, f) <= width) { cur.Clear().Append(cand); continue; }
                if (cur.Length > 0) { lines.Add(cur.ToString()); cur.Clear(); }
                var wd = word;
                while (Measure(wd, size, f) > width && wd.Length > 1)
                {
                    int n = wd.Length - 1;
                    while (n > 1 && Measure(wd[..n], size, f) > width) n--;
                    lines.Add(wd[..n]);
                    wd = wd[n..];
                }
                cur.Append(wd);
            }
            lines.Add(cur.ToString());
        }
        return lines;
    }

    /// <summary>Largura do texto em pontos. Courier é exata (0,6 em); Helvetica usa as larguras do AFM padrão.</summary>
    public static double Measure(string s, double size, Font f)
    {
        if (f == Font.Mono) return s.Length * 0.6 * size;
        double units = 0;
        foreach (char ch in s) units += CharWidth(ch, f == Font.Bold);
        return units / 1000 * size;
    }

    // Larguras (1/1000 em) da Helvetica e Helvetica-Bold, ASCII 32-126, do AFM da Adobe.
    private static readonly short[] HelvW =
    {
        278,278,355,556,556,889,667,191,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,278,278,584,584,584,556,
        1015,667,667,722,722,667,611,778,722,278,500,667,556,833,722,778,667,778,722,667,611,722,667,944,667,667,611,278,278,278,469,556,
        333,556,556,500,556,556,278,556,556,222,222,500,222,833,556,556,556,556,333,500,278,556,500,722,500,500,500,334,260,334,584,
    };
    private static readonly short[] HelvBW =
    {
        278,333,474,556,556,889,722,238,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,333,333,584,584,584,611,
        975,722,722,722,722,667,611,778,722,278,556,722,611,833,722,778,667,778,722,667,611,722,667,944,667,667,611,333,278,333,584,556,
        333,556,611,556,611,556,333,611,611,278,278,556,278,889,611,611,611,611,389,556,333,611,556,778,556,556,500,389,280,389,584,
    };

    private static double CharWidth(char ch, bool bold)
    {
        if (ch >= 32 && ch <= 126) return (bold ? HelvBW : HelvW)[ch - 32];
        // Acentuadas têm a largura da letra base; o resto, uma média segura.
        char b = char.ToLowerInvariant(ch) switch
        {
            'á' or 'à' or 'â' or 'ã' or 'ä' => 'a', 'é' or 'ê' or 'è' => 'e', 'í' or 'î' => 'i',
            'ó' or 'ô' or 'õ' or 'ö' => 'o', 'ú' or 'ü' => 'u', 'ç' => 'c', _ => '\0'
        };
        if (b != '\0') { if (char.IsUpper(ch)) b = char.ToUpperInvariant(b); return (bold ? HelvBW : HelvW)[b - 32]; }
        return ch switch { '°' => 400, '·' => 278, '–' => 556, '—' => 1000, '×' => 584, _ => 600 };
    }

    private static string FontName(Font f) => f switch { Font.Bold => "F2", Font.Mono => "F3", _ => "F1" };

    /// <summary>Texto para string do PDF em WinAnsi: escapa \ ( ) e troca o que não existe na tabela por "?".</summary>
    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char ch in s)
        {
            char c = ch switch
            {
                '–' => (char)0x96, '—' => (char)0x97, '…' => (char)0x85, '•' => (char)0x95,
                '‘' => (char)0x91, '’' => (char)0x92, '“' => (char)0x93, '”' => (char)0x94, '€' => (char)0x80,
                _ => ch <= 0xFF ? ch : '?'
            };
            if (c is '\\' or '(' or ')') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ arquivo

    public byte[] ToBytes()
    {
        var latin1 = Encoding.Latin1; // os caracteres já foram mapeados para 0-255 em Escape
        var ms = new MemoryStream();
        var offsets = new List<long>();
        void W(string s) { var b = latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Obj(string body) { offsets.Add(ms.Position); W($"{offsets.Count} 0 obj\n{body}\nendobj\n"); }

        W("%PDF-1.4\n%âãÏÓ\n");
        int n = _pages.Count;
        // 1 catálogo, 2 páginas, 3-5 fontes, depois (página, conteúdo) para cada página.
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, n).Select(i => $"{6 + 2 * i} 0 R"));
        Obj($"<< /Type /Pages /Kids [{kids}] /Count {n} >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>");
        for (int i = 0; i < n; i++)
        {
            var content = new StringBuilder(_pages[i].ToString());
            // Rodapé: texto fixo à esquerda, "página x de n" à direita.
            content.Append($"BT /F1 8 Tf 0.37 0.37 0.37 rg {N(Margin)} {N(Margin - 20)} Td ({Escape(_footer)}) Tj ET\n");
            string pg = $"página {i + 1} de {n}";
            content.Append($"BT /F1 8 Tf 0.37 0.37 0.37 rg {N(PageW - Margin - Measure(pg, 8, Font.Regular))} {N(Margin - 20)} Td ({Escape(pg)}) Tj ET\n");
            Obj($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageW)} {N(PageH)}] " +
                "/Resources << /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R >> >> " +
                $"/Contents {7 + 2 * i} 0 R >>");
            var bytes = latin1.GetBytes(content.ToString());
            Obj($"<< /Length {bytes.Length} >>\nstream\n{content}endstream");
        }
        long xref = ms.Position;
        W($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    public void Save(string path) => File.WriteAllBytes(path, ToBytes());
}
