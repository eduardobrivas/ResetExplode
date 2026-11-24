using System.Collections.ObjectModel;
using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

/// <summary>
/// Encapsula o LibreHardwareMonitorLib. A leitura do hardware roda numa thread própria (<see cref="SensorHub"/>);
/// <see cref="Refresh"/>, chamado pela janela, só copia o último resultado pronto para as linhas da tela.
/// </summary>
public sealed class SensorMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly SensorTable _table = new();
    private SensorHub? _hub;
    private volatile bool _open;
    // Preferências guardadas até o hub existir (a detecção do hardware leva alguns segundos).
    private bool _suspended, _detail;
    private SensorMode _mode = SensorMode.Normal;

    public ObservableCollection<SensorRow> Rows => _table.Rows;
    public bool IsOpen => _open;

    public SensorMonitor()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsControllerEnabled = true
        };
    }

    /// <summary>
    /// Detecta o hardware (leva de 0,5 a alguns segundos) e liga a thread de leitura. Até terminar, Refresh não faz nada.
    /// Retorna quando a primeira leitura completa está pronta, para a tela já abrir com valores.
    /// </summary>
    public async Task OpenAsync()
    {
        await Task.Run(() => _computer.Open());
        var hub = new SensorHub(_computer.Hardware.ToList()) { Suspended = _suspended, Mode = _mode, DetailWanted = _detail };
        hub.Start();
        _hub = hub;
        for (int i = 0; i < 40 && hub.Latest == null; i++) await Task.Delay(50); // primeira volta (até 2 s)
        _open = true;
    }

    /// <summary>Aplica o último resultado da thread de sensores (barato: nada de driver aqui).</summary>
    public void Refresh()
    {
        if (_open) _table.Apply(_hub?.Latest);
    }

    /// <summary>Pausa total da leitura (benchmarks de GPU e de disco).</summary>
    public bool Suspended
    {
        get => _suspended;
        set { _suspended = value; if (_hub != null) _hub.Suspended = value; }
    }

    /// <summary>Economy: janela minimizada sem overlay, testes ou iluminação. Lê 5x menos.</summary>
    public SensorMode Mode
    {
        get => _mode;
        set { _mode = value; if (_hub != null) _hub.Mode = value; }
    }

    /// <summary>Página com a lista de sensores (ou discos) aberta: ventoinhas, tensões e discos atualizam mais rápido.</summary>
    public bool DetailWanted
    {
        get => _detail;
        set { _detail = value; if (_hub != null) _hub.DetailWanted = value; }
    }

    /// <summary>Alguma página mostra as linhas de sensores? Se não, os valores andam sem redesenhar a tela.</summary>
    public bool NotifyUi
    {
        get => _table.NotifyUi;
        set => _table.NotifyUi = value;
    }

    /// <summary>Custo da coleta no último resultado (para a barra de status).</summary>
    public SensorHubStats? Stats => _hub?.Latest?.Stats;

    /// <summary>Busca em cache por hardware + tipo + nome exato.</summary>
    public SensorRow? Find(string hardware, SensorType type, string name) => _table.Find(hardware, type, name);

    /// <summary>Temperatura principal da CPU: Package (Intel) / Tctl-Tdie (AMD), senão a maior entre os núcleos.</summary>
    public float? CpuTemperature => _table.CpuTemperature;

    /// <summary>Carga total da CPU em %.</summary>
    public float? CpuLoad => _table.CpuLoad;

    /// <summary>Consumo do pacote da CPU em W.</summary>
    public float? CpuPower => _table.CpuPower;

    /// <summary>
    /// Driver PawnIO (necessário para temperatura/consumo da CPU via MSR/SMN).
    /// A biblioteca procura exatamente esta chave de desinstalação.
    /// </summary>
    public static bool IsPawnIOInstalled
    {
        get
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,
                                                                     Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key != null;
        }
    }

    public static bool IsElevated =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public enum CpuSensorProblem { None, NotElevated, PawnIOMissing, ReadFailed }

    /// <summary>Por que a temperatura/consumo da CPU não aparecem (o motivo mais provável primeiro).</summary>
    public CpuSensorProblem DiagnoseCpuSensors()
    {
        if (CpuTemperature != null) return CpuSensorProblem.None;
        if (!IsElevated) return CpuSensorProblem.NotElevated;
        if (!IsPawnIOInstalled) return CpuSensorProblem.PawnIOMissing;
        return CpuSensorProblem.ReadFailed;
    }

    public static string Explain(CpuSensorProblem p) => p switch
    {
        CpuSensorProblem.NotElevated =>
            "O RESET EXPLODE não está rodando como administrador, e o Windows só libera os sensores internos da CPU " +
            "nesse modo. Feche e abra de novo aceitando o pedido de administrador.",
        CpuSensorProblem.PawnIOMissing =>
            "Falta o driver PawnIO, que lê a temperatura e o consumo da CPU. Ele é gratuito, assinado e usado também " +
            "pelo LibreHardwareMonitor e pelo FanControl. Depois de instalar, feche e abra o RESET EXPLODE.",
        CpuSensorProblem.ReadFailed =>
            "O PawnIO está instalado, mas a leitura falhou. Se acabou de instalar, reinicie o PC. Se continuar, " +
            "confira se o antivírus não bloqueou o driver ou se a CPU é muito antiga/recente para a biblioteca de sensores.",
        _ => ""
    };

    public void Dispose()
    {
        _hub?.Dispose(); // primeiro para a thread de leitura, depois solta o hardware
        if (_open) _computer.Close();
    }
}
