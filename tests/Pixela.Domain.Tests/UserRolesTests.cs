using Pixela.Domain.Users;

namespace Pixela.Domain.Tests;

public sealed class UserRolesTests
{
    [Theory]
    [InlineData(UserRole.AgencyOwner, true)]
    [InlineData(UserRole.AgencyAdmin, true)]
    [InlineData(UserRole.ClientAdmin, false)]
    [InlineData(UserRole.Operator, false)]
    public void Solo_los_roles_de_agencia_son_de_agencia(UserRole role, bool expected) => Assert.Equal(expected, role.IsAgency());
}
