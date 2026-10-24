using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class OperacionesV4Tests
{
    private static (AparcarDb Db,string Folder) Preparar()
    {
        var dir=Path.Combine(Path.GetTempPath(),"AparcarTests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=new AparcarDb(Path.Combine(dir,"aparcar.db"));
        db.Inicializar();
        return(db,dir);
    }

    private static void Finalizar(string dir)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(dir,true);
    }

    private static CotizacionOperativa Cotizar(AparcarDb db,string patente,string tipo)
    {
        var ticket=db.RegistrarIngreso(patente,tipo,"General");
        var estadia=db.BuscarActiva(ticket.ToString())!;
        using (var sql=new SqliteConnection($"Data Source={db.RutaDb}"))
        {
            sql.Open();
            using var cmd=sql.CreateCommand();
            cmd.CommandText="UPDATE Estadias SET IngresoUtc=$fecha WHERE Id=$id";
            cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.AddMinutes(-80).ToString("O"));
            cmd.Parameters.AddWithValue("$id",estadia.Id.ToString("D"));
            cmd.ExecuteNonQuery();
        }
        return db.CotizarSalida(estadia.Id);
    }

    [Fact]
    public void PagosExternos_NoAumentanEfectivoNiPermitenDobleCobro()
    {
        var (db,dir)=Preparar();
        try
        {
            db.AbrirTurno("CAJERO",500_000);
            var c=Cotizar(db,"AA111AA","Auto");
            Assert.Throws<InvalidOperationException>(()=>db.ConfirmarPago(
                c,MediosPago.Transferencia,c.ImporteCentavos,"",false,Guid.NewGuid()));
        }
        finally { Finalizar(dir); }
    }

    [Fact]
    public void Conciliacion_Separa_Electronicos_Depositos_Retiros()
    {
        var (db,dir)=Preparar();
        try
        {
            db.AbrirTurno("Operador Uno",500_000);
            var c=Cotizar(db,"AB123BC","Auto");
            var operacion=Guid.NewGuid();
            Assert.Throws<InvalidOperationException>(()=>db.ConfirmarPago(c,
                MediosPago.Transferencia,c.ImporteCentavos,"",true,operacion));
            Assert.Throws<InvalidOperationException>(()=>db.ConfirmarPago(c,
                MediosPago.Transferencia,c.ImporteCentavos,"REF789",false,operacion));
            var pagado=db.ConfirmarPago(c,MediosPago.Transferencia,
                c.ImporteCentavos,"REF789",true,operacion);
            Assert.Equal("Transferencia",pagado.MedioPago);
            Assert.Equal(pagado.Id,db.ConfirmarPago(c,MediosPago.Transferencia,
                c.ImporteCentavos,"REF789",true,operacion).Id);
            Assert.Empty(db.ListarActivas());
            Assert.Throws<InvalidOperationException>(()=>db.ConfirmarPago(c,
                MediosPago.Efectivo,c.ImporteCentavos,"",false,Guid.NewGuid()));

            var c2=Cotizar(db,"AC456BC","Moto");
            db.ConfirmarPago(c2,MediosPago.Efectivo,c2.ImporteCentavos,
                "",false,Guid.NewGuid());
            var resumen=db.ObtenerResumenCaja()!;
            Assert.Equal(300_000,resumen.CobrosElectronicosCentavos);
            Assert.Equal(150_000,resumen.CobrosEfectivoCentavos);
            Assert.Equal(650_000,resumen.EfectivoEsperadoCentavos);
            db.RegistrarMovimientoManual("Deposito",120_000,"Refuerzo de caja","Operador Uno");
            db.RegistrarMovimientoManual("Retiro",40_000,"Retiro autorizado","Operador Uno");
            Assert.Throws<InvalidOperationException>(()=>db.RegistrarMovimientoManual(
                "Retiro",99999999,"Retiro injustificado","Operador Uno"));
            Assert.Equal(730_000,db.ObtenerResumenCaja()!.EfectivoEsperadoCentavos);
            var reporte=db.ReporteRecaudacion(DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            Assert.Equal(450_000,reporte.CobrosBrutosCentavos);
            Assert.Equal(150_000,reporte.EfectivoCentavos);
            Assert.Equal(300_000,reporte.ElectronicosCentavos);
            Assert.Equal(2,reporte.Lineas.Count);
            var turno=db.ObtenerTurnoAbierto()!;
            Assert.Equal(730_000,turno.EsperadoCentavos);
            Assert.Equal(0,db.CerrarTurno(turno.Id,730_000).DiferenciaCentavos);
        }
        finally { Finalizar(dir); }
    }

    [Fact]
    public void DevolucionRequierePin_SeRegistraUnaVez_YSeReflejaPorFecha()
    {
        var (db,dir)=Preparar();
        try
        {
            db.AbrirTurno("Cajero",100_000);
            var c=Cotizar(db,"AD789BC","Auto");
            var cobro=db.ConfirmarPago(c,MediosPago.Efectivo,c.ImporteCentavos,
                "",false,Guid.NewGuid());
            Assert.Throws<InvalidOperationException>(()=>db.DevolverCobroEfectivo(
                cobro.Id,"Cobro realizado por error","Supervisor","123456"));
            db.ConfigurarPinSupervisorInicial("523461");
            Assert.True(db.ExistePinSupervisor());
            Assert.Throws<InvalidOperationException>(()=>db.ConfigurarPinSupervisorInicial("000000"));
            Assert.Throws<InvalidOperationException>(()=>db.DevolverCobroEfectivo(
                cobro.Id,"Cobro realizado por error","Supervisor","123456"));
            var dev=db.DevolverCobroEfectivo(cobro.Id,
                "Cobro realizado por error","Supervisor","523461");
            Assert.Equal(cobro.ImporteCentavos,dev.ImporteCentavos);
            Assert.Throws<InvalidOperationException>(()=>db.DevolverCobroEfectivo(
                cobro.Id,"Segundo reintegro intentado","Supervisor","523461"));
            Assert.Equal(100_000,db.ObtenerResumenCaja()!.EfectivoEsperadoCentavos);
            var reporte=db.ReporteRecaudacion(DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            Assert.Equal(cobro.ImporteCentavos,reporte.CobrosBrutosCentavos);
            Assert.Equal(cobro.ImporteCentavos,reporte.DevolucionesCentavos);
            Assert.Equal(0,reporte.CobrosNetosCentavos);
            Assert.Equal(0,reporte.EfectivoCentavos);
            Assert.Equal(2,reporte.Lineas.Count);
            var copia=new AparcarDb(db.RutaDb);
            copia.Inicializar();
            Assert.Single(copia.CobrosEfectivoParaDevolver().Where(x=>x.Devuelto));
        }
        finally { Finalizar(dir); }
    }
}
