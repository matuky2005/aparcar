using System;
using System.IO;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public class EstadiasSqliteTests
{
    [Fact]
    public void ImpideDuplicadoActivo_YPermiteNuevoIngresoTrasSalida()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AparcarTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var db = new AparcarDb(Path.Combine(dir, "test.db"));
            db.Inicializar();
            var primero = db.RegistrarIngreso("AF 123 BC", "Auto", "General");
            Assert.True(primero > 0);
            Assert.Single(db.ListarActivas());
            Assert.Throws<InvalidOperationException>(() =>
                db.RegistrarIngreso("af123bc", "Auto", "General"));

            var estadia = db.BuscarActiva($"00{primero}");
            Assert.NotNull(estadia);
            Assert.Equal("AF123BC", estadia.Patente);

            var salida = db.RegistrarSalidaPrueba(estadia.Id);
            Assert.Equal(primero, salida.Ticket);
            Assert.Empty(db.ListarActivas());
            Assert.Throws<InvalidOperationException>(() => db.RegistrarSalidaPrueba(estadia.Id));

            var segundo = db.RegistrarIngreso("AF123BC", "Auto", "General");
            Assert.True(segundo > primero);
            Assert.Single(db.ListarActivas());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}
