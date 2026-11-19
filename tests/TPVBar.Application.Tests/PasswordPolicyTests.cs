using TPVBar.Application.Features.Authentication;

namespace TPVBar.Application.Tests;

public class PasswordPolicyTests
{
    [Fact]
    public void ContrasenaValida_NoDaErrores() =>
        Assert.Empty(PasswordPolicy.Validate("ana", "Cafe-con-leche-77"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("corta1")]
    [InlineData("          ")]
    [InlineData("aaaaaaaaaaaa")]
    public void ContrasenasDebiles_SeRechazan(string? password) =>
        Assert.NotEmpty(PasswordPolicy.Validate("ana", password));

    [Fact]
    public void ContrasenaConElNombreDeUsuario_SeRechaza() =>
        Assert.NotEmpty(PasswordPolicy.Validate("ana", "xxANAxx-12345"));

    [Fact]
    public void ContrasenaDemasiadoLarga_SeRechaza() =>
        Assert.NotEmpty(PasswordPolicy.Validate("ana", new string('a', PasswordPolicy.MaximumLength + 1) + "bcd"));
}
