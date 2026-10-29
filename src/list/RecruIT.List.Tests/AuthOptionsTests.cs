using Microsoft.Extensions.Configuration;
using RecruIT.List.Options;

namespace RecruIT.List.Tests;

public sealed class AuthOptionsTests
{
    private static IConfiguration CreateConfiguration(
        string? deprecatedAdminRole = null,
        params string[] adminRoles
    )
    {
        var settings = adminRoles
            .Select((role, i) => KeyValuePair.Create($"Auth:AdminRoles:{i}", (string?)role))
            .ToList();
        if (deprecatedAdminRole is not null)
        {
            settings.Add(
                KeyValuePair.Create(
                    AuthOptions.DeprecatedAdminRoleKey,
                    (string?)deprecatedAdminRole
                )
            );
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Fact]
    public void ReadAdminRoles_is_null_when_nothing_is_configured() =>
        Assert.Null(AuthOptions.ReadAdminRoles(CreateConfiguration()));

    [Fact]
    public void ReadAdminRoles_returns_the_configured_list() =>
        Assert.Equal(
            ["recruit-admin", "study-admin"],
            AuthOptions.ReadAdminRoles(CreateConfiguration(null, "recruit-admin", "study-admin"))
                ?? []
        );

    [Fact]
    public void ReadAdminRoles_falls_back_to_the_deprecated_single_role() =>
        Assert.Equal(
            ["recruit-admin"],
            AuthOptions.ReadAdminRoles(CreateConfiguration("recruit-admin")) ?? []
        );

    [Fact]
    public void ReadAdminRoles_prepends_the_deprecated_single_role_to_the_list() =>
        Assert.Equal(
            ["legacy-admin", "recruit-admin", "study-admin"],
            AuthOptions.ReadAdminRoles(
                CreateConfiguration("legacy-admin", "recruit-admin", "study-admin")
            ) ?? []
        );

    [Fact]
    public void ReadAdminRoles_skips_duplicates_and_blank_entries() =>
        Assert.Equal(
            ["recruit-admin", "study-admin"],
            AuthOptions.ReadAdminRoles(
                CreateConfiguration("recruit-admin", " ", "recruit-admin", "", "study-admin")
            ) ?? []
        );

    [Fact]
    public void ReadAdminRoles_ignores_a_blank_deprecated_single_role() =>
        Assert.Null(AuthOptions.ReadAdminRoles(CreateConfiguration("")));
}
