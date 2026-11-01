using RecruIT.List.Services.Access;
using static RecruIT.List.Tests.TestUsers;

namespace RecruIT.List.Tests;

public sealed class AdminRolesTests
{
    [Fact]
    public void Without_configured_roles_the_default_role_is_admin()
    {
        Assert.Equal([AdminRoles.DefaultRole], new AdminRoles().Roles);
        Assert.Equal([AdminRoles.DefaultRole], new AdminRoles([]).Roles);
        Assert.Equal(["admin"], AdminRoles.Default.Roles);
    }

    [Fact]
    public void Holding_any_one_configured_role_makes_a_user_an_admin()
    {
        var roles = new AdminRoles(["recruit-admin", "study-admin"]);

        Assert.True(roles.IsAdmin(CreateUser(role: "recruit-admin")));
        Assert.True(roles.IsAdmin(CreateUser(role: "study-admin")));
        Assert.False(roles.IsAdmin(CreateUser(role: "admin")));
        Assert.False(roles.IsAdmin(CreateUser(sub: "no-roles")));
    }
}
