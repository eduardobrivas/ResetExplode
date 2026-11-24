// Versão mínima das interfaces do LibreHardwareMonitorLib 0.9.6, só com os membros que o motor de sensores usa.
// Mesmos nomes, tipos e valores de enum da biblioteca real (conferidos no código-fonte da v0.9.6).
namespace LibreHardwareMonitor.Hardware;

public enum HardwareType
{
    Motherboard, SuperIO, Cpu, Memory, GpuNvidia, GpuAmd, GpuIntel, Storage, Network, Cooler,
    EmbeddedController, Psu, Battery, PowerMonitor,
}

public enum SensorType
{
    Voltage, Current, Power, Clock, Temperature, Load, Frequency, Fan, Flow, Control, Level, Factor,
    Data, SmallData, Throughput, TimeSpan, Timing, Energy, Noise, Conductivity, Humidity,
}

public class Identifier
{
    private readonly string _id;
    public Identifier(params string[] parts) => _id = "/" + string.Join("/", parts);
    public override string ToString() => _id;
}

public interface IHardware
{
    HardwareType HardwareType { get; }
    string Name { get; set; }
    ISensor[] Sensors { get; }
    IHardware[] SubHardware { get; }
    void Update();
}

public interface ISensor
{
    Identifier Identifier { get; }
    string Name { get; set; }
    SensorType SensorType { get; }
    float? Value { get; }
    float? Min { get; }
    float? Max { get; }
}
