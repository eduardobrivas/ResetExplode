using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

/// <summary>
/// Gravação de sessão de jogo: sensores a cada segundo + todos os frames do jogo, salvos ao parar em
/// Documentos\ResetExplode\Sessões\&lt;data&gt;_&lt;jogo&gt;\ (sessao.csv, frametimes.csv, relatorio.pdf).
/// Liga e desliga pelo botão na página Overlay ou por Ctrl + Shift + F9 dentro do jogo.
/// </summary>
public partial class MainWindow
{
    private const int RecHotkeyId = 0x5246; // "RF"
    private GameSession? _game;
    private Action<int, double>? _gameFrameHandler;
    private string? _lastSessionDir;
    private bool _gameSaving;

    private static string SessionsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ResetExplode", "Sessões");

    private bool GameRecording => _game?.Recording == true;

    private void RegisterRecHotkey(IntPtr hwnd) =>
        RegisterHotKey(hwnd, RecHotkeyId, 0x0002 | 0x0004 | 0x4000, 0x78); // Ctrl + Shift + F9, sem repetição

    private void BtnRec_Click(object sender, RoutedEventArgs e) => ToggleGameRecording();

    private void BtnRecFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _lastSessionDir ?? SessionsRoot;
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show($"Não foi possível abrir a pasta: {ex.Message}", "RESET EXPLODE"); }
    }

    private void ToggleGameRecording()
    {
        if (_gameSaving) return;
        if (GameRecording) StopGameRecording(); else StartGameRecording();
    }

    private void StartGameRecording()
    {
        _frames.Start(); // já roda desde a abertura; garante caso a sessão ETW tenha caído
        _game = new GameSession
        {
            CpuName = _monitor.Rows.FirstOrDefault(r => r.HardwareType == HardwareType.Cpu)?.Hardware ?? "",
            GpuName = _gpus.Count > 0 ? _gpus[0].Name : "",
            AppVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "",
        };
        var g = _game;
        _gameFrameHandler = g.OnFrame;
        _frames.FramePresented += _gameFrameHandler;
        BtnRec.Content = "PARAR E SALVAR";
        BtnRecFolder.Visibility = Visibility.Collapsed;
        RecStatus.Text = "Gravando… abra o jogo (o FPS é medido com o jogo em primeiro plano).";
        UpdateSensorDemand(); // sem modo economia durante a gravação, mesmo com o app minimizado
    }

    private void StopGameRecording()
    {
        var g = _game;
        if (g == null) return;
        if (_gameFrameHandler != null) _frames.FramePresented -= _gameFrameHandler;
        _gameFrameHandler = null;
        g.Stop();
        _gameSaving = true;
        BtnRec.IsEnabled = false;
        BtnRec.Content = "SALVANDO…";
        RecStatus.Text = $"Salvando {g.Samples.Count} s e {g.FrameCount:N0} frames…";

        // CSV de milhões de frames e o PDF levam alguns segundos: fora da thread da janela.
        Task.Run(() => g.SaveAll(SessionsRoot)).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            _gameSaving = false;
            BtnRec.IsEnabled = true;
            BtnRec.Content = "GRAVAR";
            if (t.Exception is { } ex)
            {
                RecStatus.Text = $"Não foi possível salvar a sessão: {ex.GetBaseException().Message}";
                return;
            }
            _lastSessionDir = t.Result;
            var s = GameSession.Analyze(g.GameFrames());
            RecStatus.Text = g.MainGame() is { } game && s.Frames > 0
                ? $"Salvo: {game.Name} · {g.Duration:hh\\:mm\\:ss} · {s.AvgFps:0} FPS médio · 1% low {s.Low1Fps:0} · " +
                  $"0,1% low {s.Low01Fps:0} · {s.Stutters} engasgos\n{_lastSessionDir}"
                : $"Salvo sem frames de jogo (nenhum jogo em primeiro plano com FPS).\n{_lastSessionDir}";
            BtnRecFolder.Visibility = Visibility.Visible;
            UpdateSensorDemand();
        }));
    }

    /// <summary>Chamado a cada segundo pelo timer da janela durante a gravação.</summary>
    private void RecordGameSample()
    {
        if (!GameRecording) return;
        var f = _frames.GetForegroundStats();
        var s = BottleneckAnalyzer.Sample(_monitor, _gpus.FirstOrDefault(), f);
        var (clockAvg, _) = CpuClocks();
        _game!.Add(new GameSample(
            DateTime.Now, f?.ProcessId, f?.ProcessName, f?.Fps, f?.Low1Fps, f?.FrameTimeMs,
            s.CpuTemp, s.CpuTotal, s.CpuMaxCore, _monitor.CpuPower is float cp && cp > 0 ? cp : null, clockAvg,
            s.GpuTemp, GpuRow(SensorType.Temperature, "GPU Hot Spot")?.RawValue, s.GpuLoad, MainGpuPower(),
            MainGpuClock(Nvml.ClockGraphics, "GPU Core"), MainGpuVram()?.Used, s.VramPct, s.RamPct,
            f != null ? BottleneckAnalyzer.Title(s.Classify()) : null));

        if (PageOverlay.IsVisible)
        {
            var game = f?.ProcessName ?? "aguardando jogo em primeiro plano";
            RecStatus.Text = $"Gravando {_game.Duration:hh\\:mm\\:ss} · {game} · {_game.FrameCount:N0} frames" +
                             (_game.Truncated ? " (limite de frames atingido)" : "");
        }
    }

    /// <summary>Texto "● REC" para o rodapé do overlay.</summary>
    private string? RecOverlayText() => GameRecording ? $"● REC {_game!.Duration:hh\\:mm\\:ss}" : null;

    /// <summary>Fechar o app gravando: salva antes de sair (sem perguntar, para não perder a sessão).</summary>
    private void SaveGameOnClose()
    {
        if (!GameRecording) return;
        try
        {
            if (_gameFrameHandler != null) _frames.FramePresented -= _gameFrameHandler;
            _game!.Stop();
            _game.SaveAll(SessionsRoot);
        }
        catch { }
    }
}
