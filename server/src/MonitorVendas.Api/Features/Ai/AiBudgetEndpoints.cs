namespace MonitorVendas.Api.Features.Ai;

public static class AiBudgetEndpoints
{
    public static RouteGroupBuilder MapAiBudgetEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/ai/budget", async (AiBudget budget, CancellationToken ct) =>
            Results.Ok(await budget.GetStatusAsync(ct)));

        return group;
    }
}
