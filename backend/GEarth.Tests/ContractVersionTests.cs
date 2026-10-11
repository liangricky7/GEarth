using GEarth.Core;

namespace GEarth.Tests;

public sealed class ContractVersionTests
{
    [Theory]
    [InlineData("previous_project.analytics-report.v1", true)]
    [InlineData("gearth.analytics-report.v1", true)]
    [InlineData("previous_project.analytics-report.v2", false)]
    [InlineData("previous_project.run-export.v1", false)]
    [InlineData("analytics-report.v1", false)]
    [InlineData("bad/prefix.analytics-report.v1", false)]
    [InlineData(".analytics-report.v1", false)]
    public void BrandingCannotChangeOrBypassTheVersionedWireContract(string value, bool expected)
    {
        Assert.Equal(expected, ContractVersion.Matches(value, "gearth.analytics-report.v1"));
    }
}
