using System;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class CierresZV26Tests
{
    [Fact]
    public void CerrarTurnoGeneraCierreZNumeradoYNoDuplicaElTurno()
    {
        var carpeta = Path.Combine(Path.GetTempPath(), "AparcarCierresZ", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(carpeta);
        try
        {
            var db = new AparcarDb(Path.Combine(carpeta, "aparcar.db"));
            db.Inicializar();
            db.CrearAdministradorInicial("AdminZ", "ClaveSegura2026");
            db.IniciarSesion("AdminZ", "ClaveSegura2026");
            db.GuardarSector("General", 20, ModosSector.Libre);
            db.AbrirTurno("AdminZ", 100_000);
            var ticket = db.RegistrarIngreso("ZZ111ZZ", "Auto", "General");
            var activa = db.BuscarActiva(ticket.ToString())!;
            var cotizacion = db.CotizarSalida(activa.Id);
            db.ConfirmarPago(cotizacion, MediosPago.Efectivo, cotizacion.ImporteCentavos,
                "", false, Guid.NewGuid());
            var turno = db.ObtenerTurnoAbierto()!;
            var arqueo = db.CerrarTurno(turno.Id, turno.EsperadoCentavos);

            Assert.Equal(1, arqueo.NumeroCierreZ);
            var cierre = Assert.Single(db.ListarCierresZ());
            Assert.Equal(1, cierre.Numero);
            Assert.Equal(turno.Id, cierre.TurnoId);
            Assert.Equal(0, cierre.DiferenciaCentavos);
            Assert.Throws<InvalidOperationException>(() => db.CerrarTurno(turno.Id, 0));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(carpeta)) Directory.Delete(carpeta, true);
        }
    }
}
