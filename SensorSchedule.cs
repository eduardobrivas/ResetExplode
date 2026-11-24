using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

/// <summary>Grupo de frequência de leitura. Cada tipo de hardware custa diferente para ler.</summary>
public enum SensorTier
{
    /// <summary>CPU, GPU e memória: o que aparece no overlay e nos testes.</summary>
    Fast,
    /// <summary>Placa-mãe (Super I/O), ventoinhas, fonte, bateria, rede: mudam devagar.</summary>
    Medium,
    /// <summary>Discos: cada leitura refaz o S.M.A.R.T. no driver (lento e acorda o disco).</summary>
    Slow,
}

/// <summary>
/// Normal: app aberto ou algo rodando (overlay, stress, iluminação).
/// Economy: janela minimizada sem nada que dependa dos sensores.
/// </summary>
public enum SensorMode { Normal, Economy }

/// <summary>
/// Regras de quando cada hardware é lido. Sem estado e sem dependência de janela, para dar para testar.
/// </summary>
public sealed class SensorSchedule
{
    /// <summary>Divide todos os intervalos (só para testes: 10 = dez vezes mais rápido).</summary>
    public double Speedup { get; init; } = 1;

    public static SensorTier TierOf(HardwareType t) => t switch
    {
        HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.Memory
            => SensorTier.Fast,
        HardwareType.Storage => SensorTier.Slow,
        _ => SensorTier.Medium,
    };

    /// <summary>
    /// Intervalo entre leituras. <paramref name="detail"/> = alguma tela que mostra a lista completa de sensores está aberta,
    /// então ventoinhas e tensões também precisam andar a cada segundo.
    /// </summary>
    public TimeSpan Interval(SensorTier tier, SensorMode mode, bool detail)
    {
        double ms = (tier, mode) switch
        {
            (SensorTier.Fast, SensorMode.Normal) => 1_000,
            (SensorTier.Medium, SensorMode.Normal) => detail ? 1_000 : 2_000,
            (SensorTier.Slow, SensorMode.Normal) => detail ? 3_000 : 15_000,
            (SensorTier.Fast, SensorMode.Economy) => 5_000,
            (SensorTier.Medium, SensorMode.Economy) => 10_000,
            (SensorTier.Slow, SensorMode.Economy) => 60_000,
            _ => 1_000,
        };
        return TimeSpan.FromMilliseconds(ms / Speedup);
    }

    /// <summary>Já passou o intervalo desde a última leitura? (lastRead = null: nunca lido.)</summary>
    public static bool IsDue(TimeSpan? lastRead, TimeSpan now, TimeSpan interval) =>
        lastRead is not TimeSpan last || now - last >= interval;
}
