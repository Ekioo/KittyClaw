using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

public class AgentLanguageInstructionTests
{
    [Theory]
    [InlineData("en", "English (en)")]
    [InlineData("fr", "French (fr)")]
    [InlineData("es", "Spanish (es)")]
    [InlineData("de", "German (de)")]
    [InlineData("it", "Italian (it)")]
    [InlineData("pt-BR", "Brazilian Portuguese (pt-BR)")]
    [InlineData("ja", "Japanese (ja)")]
    [InlineData("zh-CN", "Simplified Chinese (zh-CN)")]
    [InlineData("ZH-cn", "Simplified Chinese (zh-CN)")]
    public void Instruction_reflects_supported_UI_language(string language, string expected)
    {
        Assert.Contains(expected, AgentRunner.BuildLanguageInstruction(language));
    }

    [Fact]
    public void Unknown_UI_language_uses_English_instruction()
    {
        Assert.Contains("English (en)", AgentRunner.BuildLanguageInstruction("unknown"));
    }
}
