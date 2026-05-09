using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// Verifies that every backend node type registered via [JsonDerivedType] has a matching
/// UiTypeKey implementation, and that the key matches the JSON discriminator.
/// Add a [JsonDerivedType] without implementing UiTypeKey and the project won't compile.
/// Add a [JsonDerivedType] with a mismatched UiTypeKey and this test fails.
/// </summary>
public class NodeRegistryTests
{
    [Fact]
    public void All_ActionSpec_subtypes_have_matching_UiTypeKey()
    {
        AssertUiTypeKeysMatchDiscriminators<ActionSpec>();
    }

    [Fact]
    public void All_TriggerSpec_subtypes_have_matching_UiTypeKey()
    {
        AssertUiTypeKeysMatchDiscriminators<TriggerSpec>();
    }

    [Fact]
    public void All_ConditionSpec_subtypes_have_matching_UiTypeKey()
    {
        AssertUiTypeKeysMatchDiscriminators<ConditionSpec>();
    }

    private static void AssertUiTypeKeysMatchDiscriminators<TBase>() where TBase : class
    {
        var attrs = typeof(TBase)
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .ToList();

        Assert.NotEmpty(attrs);

        var mismatches = new List<string>();
        foreach (var attr in attrs)
        {
            var expectedKey = (string)attr.TypeDiscriminator!;
            var instance = (TBase)RuntimeHelpers.GetUninitializedObject(attr.DerivedType);

            var prop = attr.DerivedType.GetProperty("UiTypeKey");
            Assert.NotNull(prop);

            var actualKey = (string)prop.GetValue(instance)!;
            if (actualKey != expectedKey)
                mismatches.Add($"{attr.DerivedType.Name}: UiTypeKey='{actualKey}' but JsonDerivedType discriminator='{expectedKey}'");
        }

        if (mismatches.Count > 0)
            Assert.Fail("UiTypeKey mismatch(es):\n" + string.Join("\n", mismatches));
    }
}
