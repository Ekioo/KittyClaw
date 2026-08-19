namespace KittyClaw.Web.Components.Pages;

public static class CostDisplayTotal
{
    public static decimal Sum(IEnumerable<decimal> costs) =>
        costs.Sum(cost => decimal.Round(cost, 2));
}
