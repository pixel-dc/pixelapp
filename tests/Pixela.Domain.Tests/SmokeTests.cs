namespace Pixela.Domain.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void El_proyecto_de_dominio_compila() => Assert.NotNull(typeof(SmokeTests).Assembly);
}
