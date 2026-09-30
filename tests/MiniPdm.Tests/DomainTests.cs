using MiniPdm.Domain;

namespace MiniPdm.Tests;

public class DomainTests
{
    [Theory]
    [InlineData("РДЦЛ.304112.300", true)]
    [InlineData("PДЦЛ.304112.300", false)]
    [InlineData("РДЦЛ.30411.300", false)]
    [InlineData("РДЦЛ.304112.30", false)]
    [InlineData("рдцл.304112.300", false)]
    [InlineData(null, false)]
    public void Обозначение_проверяется_строго(string? value, bool expected)
        => Assert.Equal(expected, Designation.IsValid(value));

    [Theory]
    [InlineData(VersionState.InWork, VersionState.Approved, true)]
    [InlineData(VersionState.InWork, VersionState.Cancelled, true)]
    [InlineData(VersionState.Approved, VersionState.Cancelled, true)]
    [InlineData(VersionState.Approved, VersionState.InWork, false)]
    [InlineData(VersionState.Cancelled, VersionState.InWork, false)]
    [InlineData(VersionState.Cancelled, VersionState.Approved, false)]
    [InlineData(VersionState.InWork, VersionState.InWork, false)]
    public void Разрешены_только_прямые_переходы(VersionState from, VersionState to, bool expected)
        => Assert.Equal(expected, VersionRules.CanTransition(from, to));
}
