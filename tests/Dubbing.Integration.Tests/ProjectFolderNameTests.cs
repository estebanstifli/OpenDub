using Dubbing.App.Services;

namespace Dubbing.Integration.Tests;

public class ProjectFolderNameTests
{
    [Theory]
    [InlineData("THE BATMAN – Main Trailer", "THE-BATMAN-Main-Trailer")]
    [InlineData("Acción: niño / película", "Accion-nino-pelicula")]
    [InlineData("../../CON", "Project-CON")]
    [InlineData("日本語🎬", "Project")]
    public void NormalizesPortableFolderNames(string input, string expected) => Assert.Equal(expected, ProjectFolderName.Normalize(input));
    [Fact] public void BoundsLongNames() => Assert.Equal(80, ProjectFolderName.Normalize(new string('a', 400)).Length);
}
