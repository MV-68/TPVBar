using TPVBar.Domain.Invoicing;

namespace TPVBar.Domain.Tests;

public class SpanishTaxIdTests
{
    [Theory]
    [InlineData("12345678Z")] // DNI
    [InlineData("00000000T")]
    [InlineData("12345678z")]
    [InlineData(" 12345678-Z ")]
    [InlineData("X1234567L")] // NIE
    [InlineData("Y1234567X")]
    [InlineData("Z1234567R")]
    [InlineData("A58818501")] // CIF con control numérico
    [InlineData("B12345674")]
    [InlineData("Q2826000H")] // CIF con control en letra (organismos)
    [InlineData("P2807900B")]
    public void IsValid_AceptaIdentificadoresConDigitoDeControlCorrecto(string id) =>
        Assert.True(SpanishTaxId.IsValid(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345678A")] // letra incorrecta
    [InlineData("1234567Z")] // corto
    [InlineData("123456789Z")] // largo
    [InlineData("X1234567A")]
    [InlineData("B12345675")] // control erróneo
    [InlineData("Q2826000A")] // organismos: exige letra correcta
    [InlineData("A5881850I")] // control en letra donde exige dígito
    [InlineData("I12345674")] // letra inicial no válida
    [InlineData("ABCDEFGHI")]
    public void IsValid_RechazaIdentificadoresInvalidos(string? id) =>
        Assert.False(SpanishTaxId.IsValid(id));

    [Fact]
    public void Normalize_PasaAMayusculasYQuitaEspaciosYGuiones() =>
        Assert.Equal("B12345674", SpanishTaxId.Normalize(" b-1234 5674 "));
}
