namespace KittyClaw.Web.Components.Pages;

public static class CostDisplayTotal
{
    public static decimal Sum(IEnumerable<decimal> costs) =>
        costs.Sum(cost => decimal.Round(cost, cost is > 0 and < 0.01m ? 6 : 2));
}
