using System.Collections.ObjectModel;
using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

/// <summary>
/// Lado da janela: transforma o último <see cref="SensorSnapshot"/> nas linhas da tela (<see cref="Rows"/>)
/// e responde às buscas por sensor sem varrer a lista inteira a cada vez.
/// Só é usado na thread da janela.
/// </summary>
public sealed class SensorTable
{
    private readonly Dictionary<string, SensorRow> _byId = new();
    private readonly Dictionary<(string Hw, SensorType Type, string Name), SensorRow?> _find = new();
    private long _appliedVersion;
    private bool _notify = true;

    public ObservableCollection<SensorRow> Rows { get; } = new();

    /// <summary>Muda sempre que uma linha nova entra (as buscas em cache dependem disso).</summary>
    public int RowsVersion { get; private set; }

    /// <summary>
    /// Avisar a tela a cada mudança? Desligado quando nenhuma página mostra a lista: os valores continuam
    /// sendo atualizados (overlay, painel e testes leem RawValue), só não disparam o redesenho.
    /// </summary>
    public bool NotifyUi
    {
        get => _notify;
        set
        {
            if (_notify == value) return;
            _notify = value;
            if (value) foreach (var r in Rows) r.NotifyAll(); // a página abriu: mostra já os valores atuais
        }
    }

    /// <summary>Aplica o snapshot. Retorna false se ele já tinha sido aplicado.</summary>
    public bool Apply(SensorSnapshot? snap)
    {
        if (snap == null || snap.Version == _appliedVersion) return false;
        _appliedVersion = snap.Version;
        foreach (var r in snap.Readings)
        {
            if (!_byId.TryGetValue(r.Id, out var row))
            {
                row = new SensorRow
                {
                    Id = r.Id, Hardware = r.Hardware, Category = SensorRow.CategoryName(r.Type),
                    Name = r.Name, Type = r.Type, HardwareType = r.HardwareType,
                };
                _byId[r.Id] = row;
                Rows.Add(row);
                RowsVersion++;
                _find.Clear(); // uma busca que deu "não existe" pode existir agora
                _cpuTemps = null;
            }
            row.Update(r.Value, r.Min, r.Max, _notify);
        }
        return true;
    }

    /// <summary>Sensor pelo hardware, tipo e nome exatos (ex.: GPU principal, Load, "GPU Core"). Em cache.</summary>
    public SensorRow? Find(string hardware, SensorType type, string name)
    {
        var key = (hardware, type, name);
        if (!_find.TryGetValue(key, out var row))
            _find[key] = row = Rows.FirstOrDefault(r => r.Hardware == hardware && r.Type == type && r.Name == name);
        return row;
    }

    /// <summary>Primeiro sensor de um tipo de hardware com o nome exato (ex.: Cpu, Load, "CPU Total"). Em cache.</summary>
    public SensorRow? Find(HardwareType hwType, SensorType type, string name)
    {
        var key = ("\u0001" + hwType, type, name);
        if (!_find.TryGetValue(key, out var row))
            _find[key] = row = Rows.FirstOrDefault(r => r.HardwareType == hwType && r.Type == type && r.Name == name);
        return row;
    }

    private List<SensorRow>? _cpuTemps;
    private SensorRow? _cpuMainTemp;

    /// <summary>Temperatura principal da CPU: Package (Intel) / Tctl-Tdie (AMD), senão a maior entre os núcleos.</summary>
    public float? CpuTemperature
    {
        get
        {
            if (_cpuTemps == null)
            {
                _cpuTemps = Rows.Where(r => r.HardwareType == HardwareType.Cpu && r.Type == SensorType.Temperature).ToList();
                _cpuMainTemp = _cpuTemps.FirstOrDefault(r => r.Name.Contains("Package") || r.Name.Contains("Tctl") || r.Name.Contains("Tdie"));
            }
            if (_cpuMainTemp?.RawValue is > 0) return _cpuMainTemp.RawValue;
            float? max = null;
            foreach (var r in _cpuTemps)
                if (r.RawValue is > 0 && (max == null || r.RawValue > max)) max = r.RawValue;
            return max;
        }
    }

    public float? CpuLoad => Find(HardwareType.Cpu, SensorType.Load, "CPU Total")?.RawValue;

    private SensorRow? _cpuPower;
    private int _cpuPowerVersion = -1;

    /// <summary>Consumo do pacote da CPU em W ("Package" no AMD, "CPU Package" no Intel). Mesmo critério de antes, em cache.</summary>
    public float? CpuPower
    {
        get
        {
            if (_cpuPowerVersion != RowsVersion)
            {
                _cpuPower = Rows.FirstOrDefault(r => r.HardwareType == HardwareType.Cpu && r.Type == SensorType.Power && r.Name.Contains("Package"));
                _cpuPowerVersion = RowsVersion;
            }
            return _cpuPower?.RawValue;
        }
    }
}
