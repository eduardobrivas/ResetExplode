using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ResetExplode;

/// <summary>Parte da janela: aplica a cor do estado (ver StatusLevel.cs) no texto, ligada ao pincel do tema.</summary>
public static partial class StatusColors
{
    public static void Apply(FrameworkElement e, StatusLevel l) =>
        e.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, Key(l));
}

/// <summary>Cor da linha do registro do stress test (verde = concluído/ciclo ok, amarelo = aviso, vermelho = falha).</summary>
public sealed class LogLineBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = StatusColors.LogLine(value as string ?? "");
        // Linha comum fica com a cor de "texto secundário" do registro.
        return Application.Current.TryFindResource(level == StatusLevel.Normal ? "Muted" : StatusColors.Key(level));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
