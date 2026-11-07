using SreAgent.Core;

namespace SreAgent.Core.Tests;

public class AnalysisParserTests
{
    private const string Body = """{"summary":"s","hypothesis":"postgres down","confidence":"HIGH","evidence":["pg_up=0"],"recommendations":"restart","next_checks":[]}""";

    [Theory]
    [InlineData(Body)]
    [InlineData("```json\n" + Body + "\n```")]
    [InlineData("<think>let me reason about pg_up {not json}</think>\n" + Body)]
    [InlineData("Here is my analysis:\n" + Body + "\nHope this helps.")]
    public void Accepts_the_shapes_small_models_actually_produce(string text)
    {
        var a = AnalysisParser.Parse(text);

        Assert.True(a.Parsed);
        Assert.Equal("postgres down", a.Hypothesis);
        Assert.Equal("high", a.Confidence);          // normalised
        Assert.Equal(["restart"], a.Recommendations); // a string where a list was expected
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("the database seems to be down")]
    [InlineData("{\"summary\": \"no hypothesis field\"}")]
    [InlineData("{broken json")]
    public void Anything_else_is_marked_unparsed_never_thrown(string? text)
    {
        var a = AnalysisParser.Parse(text);

        Assert.False(a.Parsed);
        Assert.Equal("low", a.Confidence);
    }
}
