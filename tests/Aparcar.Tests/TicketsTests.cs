using System;
using System.IO;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class TicketsTests
{
    [Fact]
    public void TicketTieneQrUnicoYSeLocalizaConLector()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AparcarTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var db = new AparcarDb(Path.Combine(dir,"aparcar.db"));
            db.Inicializar();
            db.CrearAdministradorInicial("AdminPrueba", "ClavePrueba123");
            db.IniciarSesion("AdminPrueba", "ClavePrueba123");
            var n1 = db.RegistrarIngreso("AA111AA", "Auto", "General");
            var n2 = db.RegistrarIngreso("BB222BB", "Moto", "General");
            var t1 = db.ObtenerTicket(n1);
            var t2 = db.ObtenerTicket(n2);
            Assert.Equal(48, t1.TokenQr.Length);
            Assert.NotEqual(t1.TokenQr, t2.TokenQr);
            Assert.Equal(t1.EstadiaId, db.BuscarActiva("APARCAR:" + t1.TokenQr)!.Id);
            Assert.Equal(t2.EstadiaId, db.BuscarActiva(t2.TokenQr)!.Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(dir,true); }
    }

    [Fact]
    public void LaMigracionCompletaTicketsDeEstadiasExistentes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AparcarTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var ruta = Path.Combine(dir,"aparcar.db");
        try
        {
            using (var db = new SqliteConnection($"Data Source={ruta}"))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE Estadias(
                        Ticket INTEGER PRIMARY KEY AUTOINCREMENT,
                        Id TEXT NOT NULL UNIQUE,
                        Patente TEXT NOT NULL,
                        PatenteNormalizada TEXT NOT NULL,
                        TipoVehiculo TEXT NOT NULL,
                        Sector TEXT NOT NULL,
                        IngresoUtc TEXT NOT NULL,
                        SalidaUtc TEXT,
                        Estado TEXT NOT NULL
                    );
                    INSERT INTO Estadias (Id,Patente,PatenteNormalizada,TipoVehiculo,Sector,IngresoUtc,Estado)
                    VALUES('d1af690e-4cdf-48e8-b1c8-e359798b3ae4','AF123BC','AF123BC','Auto',
                        'General','2026-10-20T13:00:00.0000000+00:00','Activa');
                    """;
                cmd.ExecuteNonQuery();
            }
            var data = new AparcarDb(ruta);
            data.Inicializar();
            data.CrearAdministradorInicial("AdminPrueba", "ClavePrueba123");
            data.IniciarSesion("AdminPrueba", "ClavePrueba123");
            var ticket = data.ObtenerTicket(1);
            Assert.Equal("AF123BC",ticket.Patente);
            Assert.Equal(48,ticket.TokenQr.Length);
            data.Inicializar();
            Assert.Equal(ticket.TokenQr,data.ObtenerTicket(1).TokenQr);
            Assert.Single(data.ListarActivas());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(dir,true); }
    }
}
