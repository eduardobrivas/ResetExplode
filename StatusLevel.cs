namespace ResetExplode;

/// <summary>Estado de um valor: normal (cor do texto), bom, atenção ou problema.</summary>
public enum StatusLevel { Normal, Good, Warn, Bad }

/// <summary>
/// Cor com significado, usada só onde o valor pede atenção. Liga o texto ao pincel do tema por referência
/// (SetResourceReference), então a troca escuro/claro atualiza na hora, como o resto da interface.
/// </summary>
public static partial class StatusColors
{
    public static string Key(StatusLevel l) => l switch
    {
        StatusLevel.Good => "Good",
        StatusLevel.Warn => "Warn",
        StatusLevel.Bad => "Bad",
        _ => "Fg",
    };

    /// <summary>
    /// Temperatura contra um limite: problema a 3 °C ou menos do limite (o corte automático está chegando),
    /// atenção a 10 °C ou menos, normal abaixo disso. Sem leitura = normal.
    /// </summary>
    public static StatusLevel Temperature(double? t, double limit) =>
        t is not double v ? StatusLevel.Normal
        : v >= limit - 3 ? StatusLevel.Bad
        : v >= limit - 10 ? StatusLevel.Warn
        : StatusLevel.Normal;

    /// <summary>Teste de RAM: erro = problema; pelo menos um ciclo completo sem erro = bom.</summary>
    public static StatusLevel Ram(long passes, long errors) =>
        errors > 0 ? StatusLevel.Bad : passes > 0 ? StatusLevel.Good : StatusLevel.Normal;

    /// <summary>Classifica uma linha do registro pelo conteúdo (o registro é texto livre).</summary>
    public static StatusLevel LogLine(string line)
    {
        if (line.Contains("ABORTADO") || line.Contains(" erros em ") || line.Contains("esperado ") && line.Contains("lido "))
            return StatusLevel.Bad;
        if (line.Contains("travada na RAM: não") || line.Contains("travada na RAM: parcial") || line.Contains("leituras falharam"))
            return StatusLevel.Warn;
        if (line.Contains("Concluído") || line.Contains("completo, 0 erros"))
            return StatusLevel.Good;
        return StatusLevel.Normal;
    }
}
