namespace MonitorVendas.Api.Features.Metrics;

// Feriado cadastrado pelo usuário: o dia inteiro fica fora do relógio útil.
public class Holiday
{
    public Guid Id { get; set; }
    public DateOnly Date { get; set; }
    public string Name { get; set; } = string.Empty;
}
