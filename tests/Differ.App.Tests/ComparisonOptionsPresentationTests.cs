using Differ.App.Resources;
using Differ.App.ViewModels;
using Differ.Core.Models;

namespace Differ.App.Tests;

public sealed class ComparisonOptionsPresentationTests
{
    [Fact]
    public void ToolbarDistinguishesDefaultAdjustedAndPendingRules()
    {
        var options = new ComparisonOptionsViewModel();
        options.SetFormat(FileKind.Text, ".txt");
        Assert.Equal(Strings.ComparisonOptions, options.ToolbarLabel);

        var notifications = new List<string?>();
        options.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        options.IgnoreCase = true;
        Assert.Equal(Strings.ComparisonRulesAdjusted, options.ToolbarLabel);
        Assert.Contains(nameof(ComparisonOptionsViewModel.ToolbarLabel), notifications);

        options.HasPendingChanges = true;
        Assert.Equal(Strings.ComparisonRulesPending, options.ToolbarLabel);
        options.ResetDefaults();
        Assert.Equal(Strings.ComparisonRulesPending, options.ToolbarLabel);
        options.HasPendingChanges = false;
        Assert.Equal(Strings.ComparisonOptions, options.ToolbarLabel);
    }

    [Fact]
    public void RulesForOtherFormatsDoNotMarkTheCurrentComparisonAsAdjusted()
    {
        var options = new ComparisonOptionsViewModel { IgnoreCase = true, CompareFormatting = true };
        options.SetFormat(FileKind.Binary, ".bin");
        Assert.False(options.HasCustomRules);
        options.SetFormat(FileKind.Excel, ".csv");
        Assert.False(options.HasCustomRules);
        options.KeyColumnsText = "A,C";
        Assert.True(options.HasCustomRules);
        options.SetFormat(FileKind.Word, ".docx");
        Assert.True(options.HasCustomRules);
        options.CompareFormatting = false;
        Assert.False(options.HasCustomRules);
    }
}
