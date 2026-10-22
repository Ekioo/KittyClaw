using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Helpers;

internal static class JevResponseFixture
{
    internal static object Answers(string destination, double clarification = 0.15) => new
    {
        routing = new { type = "choice", choice = destination, confidence = 0.9,
            probabilities = new Dictionary<string, double> { [destination] = 0.95, ["none"] = 0.05 } },
        request_type = Choice("problem", JevTriageQuestions.RequestTypes.Keys),
        complexity = Choice("moderate", JevTriageQuestions.Complexity.Keys),
        clarification = new { type = "noul", noul = clarification }
    };

    private static object Choice(string selected, IEnumerable<string> options) => new
    {
        type = "choice", choice = selected, confidence = 1.0,
        probabilities = options.ToDictionary(option => option, option => option == selected ? 1.0 : 0.0)
    };
}
