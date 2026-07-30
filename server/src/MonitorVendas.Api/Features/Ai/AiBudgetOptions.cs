namespace MonitorVendas.Api.Features.Ai;

public sealed class AiBudgetOptions
{
    public const string Section = "AiBudget";

    // Desligado, o gasto continua sendo registrado — só não bloqueia nada.
    public bool Enabled { get; set; } = true;

    public decimal AmountPerWindow { get; set; } = 20m;

    // A janela reabastece sempre à meia-noite local e a cada N horas dentro do
    // dia. O saldo não usado não acumula: cada janela começa cheia.
    public int WindowHours { get; set; } = 24;

    // Cobrado a mais sobre o custo real, para absorver variação de câmbio e de
    // preço do provedor.
    public decimal MarginPercent { get; set; } = 15m;
}
