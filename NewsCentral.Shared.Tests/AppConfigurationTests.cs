using Microsoft.Extensions.Configuration;
using NewsCentral.Configuration;

namespace NewsCentral.Shared.Tests;

public sealed class AppConfigurationTests
{
    // M5b: the admin/admin first-run seed and its two Initialization:* keys were removed —
    // AppConfiguration.DefaultAdminUsername/DefaultAdminPassword no longer exist. DataPath is
    // exercised elsewhere (EnvironmentSettingsTests.FromMachineConfiguration_MapsEveryField).

    // AuthenticationService.LoginAsync gates on AppConfiguration.EnableLocalAccounts, and no test
    // project references the MAUI app, so the gate's decision is covered here.
    private static AppConfiguration Build(params (string Key, string Value)[] values) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build());

    [Fact]
    public void EnableLocalAccounts_DefaultsToFalse_WhenAbsent()
        => Assert.False(Build().EnableLocalAccounts);

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("")]
    [InlineData("garbage")]
    public void EnableLocalAccounts_IsFalse_ForFalseOrUnparseableValues(string value)
        => Assert.False(Build(("Authentication:EnableLocalAccounts", value)).EnableLocalAccounts);

    // REG_DWORD 1 reaches the configuration binder as "True" (RegistryConfigurationProvider coercion).
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public void EnableLocalAccounts_IsTrue_WhenEnabled(string value)
        => Assert.True(Build(("Authentication:EnableLocalAccounts", value)).EnableLocalAccounts);
}
