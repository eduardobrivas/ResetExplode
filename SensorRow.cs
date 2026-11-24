using System.ComponentModel;
using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

/// <summary>Linha do painel de sensores, com notificação de mudança para a UI.</summary>
public class SensorRow : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Hardware { get; init; } = "";
    public string Category { get; init; } = "";
    public string Name { get; init; } = "";
    public SensorType Type { get; init; }
    public HardwareType HardwareType { get; init; }

    private float? _value, _min, _max;
    public float? RawValue => _value;

    /// <summary>Ordem de exibição por tipo (clocks e temperaturas primeiro, como no GPU-Z).</summary>
    public int Order => Type switch
    {
        SensorType.Clock => 0, SensorType.Temperature => 1, SensorType.Load => 2, SensorType.Fan => 3,
        SensorType.Control => 4, SensorType.Power => 5, SensorType.Voltage => 6, SensorType.Current => 7,
        SensorType.SmallData => 8, SensorType.Data => 9, SensorType.Throughput => 10, _ => 11
    };
    public string Value => Format(_value);
    public string Min => Format(_min);
    public string Max => Format(_max);

    private static readonly PropertyChangedEventArgs ValueArgs = new(nameof(Value)), MinArgs = new(nameof(Min)), MaxArgs = new(nameof(Max));

    /// <summary>
    /// Guarda os valores novos. Só avisa a tela (<paramref name="notify"/>) do que mudou de fato:
    /// antes eram 3 avisos por sensor por segundo, mesmo com a página fechada e o valor igual.
    /// </summary>
    public void Update(float? value, float? min, float? max, bool notify = true)
    {
        bool v = value != _value, lo = min != _min, hi = max != _max;
        _value = value; _min = min; _max = max;
        if (!notify || PropertyChanged == null) return;
        if (v) PropertyChanged(this, ValueArgs);
        if (lo) PropertyChanged(this, MinArgs);
        if (hi) PropertyChanged(this, MaxArgs);
    }

    /// <summary>Redesenha os três valores (quando a página volta a ficar visível).</summary>
    public void NotifyAll()
    {
        PropertyChanged?.Invoke(this, ValueArgs);
        PropertyChanged?.Invoke(this, MinArgs);
        PropertyChanged?.Invoke(this, MaxArgs);
    }

    private string Format(float? v)
    {
        if (v == null) return "—";
        return Type switch
        {
            SensorType.Temperature => $"{v:0.#} °C",
            SensorType.Load or SensorType.Control or SensorType.Level or SensorType.Humidity => $"{v:0.#} %",
            SensorType.Clock => $"{v:0} MHz",
            SensorType.Power => $"{v:0.#} W",
            SensorType.Voltage => $"{v:0.###} V",
            SensorType.Current => $"{v:0.###} A",
            SensorType.Fan => $"{v:0} RPM",
            SensorType.Flow => $"{v:0.#} L/h",
            SensorType.Frequency => $"{v:0.#} Hz",
            SensorType.Data => $"{v:0.##} GB",
            SensorType.SmallData => $"{v:0.#} MB",
            SensorType.Throughput => $"{v / 1048576f:0.##} MB/s",
            SensorType.Energy => $"{v:0.#} mWh",
            _ => $"{v:0.##}"
        };
    }

    public static string CategoryName(SensorType t) => t switch
    {
        SensorType.Temperature => "Temperatura",
        SensorType.Load => "Carga",
        SensorType.Clock => "Clock",
        SensorType.Power => "Consumo",
        SensorType.Voltage => "Tensão",
        SensorType.Current => "Corrente",
        SensorType.Fan => "Ventoinhas",
        SensorType.Control => "Controle",
        SensorType.Level => "Nível",
        SensorType.Data or SensorType.SmallData => "Dados",
        SensorType.Throughput => "Taxa",
        SensorType.Frequency => "Frequência",
        SensorType.Energy => "Energia",
        SensorType.Flow => "Fluxo",
        SensorType.Humidity => "Umidade",
        SensorType.Factor => "Fator",
        _ => t.ToString()
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}
