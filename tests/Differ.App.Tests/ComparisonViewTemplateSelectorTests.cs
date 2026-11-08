using Differ.App.Controls;

namespace Differ.App.Tests;

public sealed class ComparisonViewTemplateSelectorTests
{
    [Theory]
    [InlineData(true, false, false, (int)ComparisonViewKind.Spreadsheet)]
    [InlineData(false, true, false, (int)ComparisonViewKind.Text)]
    [InlineData(false, false, true, (int)ComparisonViewKind.Word)]
    [InlineData(false, false, false, (int)ComparisonViewKind.Standard)]
    public void GetViewKindSelectsExactlyOneFormatView(
        bool isSpreadsheet,
        bool isText,
        bool isWord,
        int expected)
    {
        var actual = ComparisonViewTemplateSelector.GetViewKind(
            isSpreadsheet,
            isText,
            isWord);

        Assert.Equal(expected, (int)actual);
    }
}
