using System;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class AvanzadoV7Tests
{
    private static (AparcarDb Db,string Carpeta) Crear()
    {
        var dir=Path.Combine(Path.GetTempPath(),"AparcarV7",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=new AparcarDb(Path.Combine(dir,"aparcar.db"));
        db.Inicializar();
        db.CrearAdministradorInicial("AdminV7","ClaveSegura2026");
        db.IniciarSesion("AdminV7","ClaveSegura2026");
        return(db,dir);
    }

    private static void Limpiar(string dir)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(dir,true);
    }

    [Fact]
    public void NocturnoSoloCubreUnaNocheCompleta_ConZonaDefinida()
    {
        var zona=TimeZoneInfo.CreateCustomTimeZone("TucumanPruebas",TimeSpan.FromHours(-3),"Tucuman","Tucuman");
        var entrada=new DateTimeOffset(2026,10,24,21,30,0,TimeSpan.FromHours(-3));
        var salida=new DateTimeOffset(2026,10,25,7,45,0,TimeSpan.FromHours(-3));
        Assert.True(ModalidadesAbono.CubreHorario(ModalidadesAbono.Nocturna,entrada,salida,zona));
        Assert.False(ModalidadesAbono.CubreHorario(ModalidadesAbono.Nocturna,
            entrada,salida.AddHours(1),zona));
        Assert.False(ModalidadesAbono.CubreHorario(ModalidadesAbono.Nocturna,
            entrada.AddHours(-2),salida,zona));
        Assert.True(ModalidadesAbono.CubreHorario(ModalidadesAbono.Completa,
            entrada,entrada.AddDays(1),zona));
    }

    [Fact]
    public void MigracionV7ConservaContratoPrevioYAgregaPatentePrincipal()
    {
        var (db,dir)=Crear();
        try
        {
            var p=db.GuardarPlanAbono("Completo","Auto","Mensual",1_000_000);
            var c=db.CrearContratoPendiente("Prueba Uno","AF123BC",p.Id);
            var antes=db.ListarVehiculosAbono(c.Id);
            Assert.Single(antes);
            var segunda=new AparcarDb(db.RutaDb);
            segunda.Inicializar();
            Assert.Single(segunda.ListarVehiculosAbono(c.Id));
            Assert.Equal("Completa",segunda.ListarContratosAbono().Single().Modalidad);
        }
        finally{Limpiar(dir);}
    }

    [Fact]
    public void FlotaYRenovacion_NoSeSolapanYNoDuplicanCobertura()
    {
        var (db,dir)=Crear();
        try
        {
            db.AbrirTurno("Cajero",0);
            var plan=db.GuardarPlanAbono("Mensual flota","Auto","Mensual",2_000_000);
            var contrato=db.CrearContratoPendiente("Empresa Uno","AF111AA",plan.Id);
            db.AgregarVehiculoAbono(contrato.Id,"AF222BB");
            Assert.Equal(2,db.ListarVehiculosAbono(contrato.Id).Count);
            Assert.Throws<InvalidOperationException>(()=>db.AgregarVehiculoAbono(contrato.Id,"AF222BB"));
            var pago=db.CobrarComplemento("Abono",contrato.Id,MediosPago.Efectivo,2_000_000,
                "",false,Guid.NewGuid());
            Assert.Equal(2_000_000,pago.ImporteCentavos);
            Assert.NotNull(db.BuscarCobertura("AF222BB","Auto",DateTimeOffset.UtcNow));
            Assert.Throws<InvalidOperationException>(()=>db.AgregarVehiculoAbono(contrato.Id,"AF333CC"));

            var renovación=db.CrearRenovacion(contrato.Id,plan.Id);
            Assert.Equal("Pendiente",renovación.Estado);
            Assert.Equal(2,db.ListarVehiculosAbono(renovación.Id).Count);
            db.CobrarComplemento("Abono",renovación.Id,MediosPago.Efectivo,2_000_000,
                "",false,Guid.NewGuid());
            var contratos=db.ListarContratosAbono();
            var original=contratos.Single(c=>c.Id==contrato.Id);
            var nuevo=contratos.Single(c=>c.Id==renovación.Id);
            Assert.Equal(original.FinUtc,nuevo.InicioUtc);
            Assert.Equal(original.FinUtc!.Value.AddMonths(1),nuevo.FinUtc);
            Assert.Single(contratos.Where(x=>x.Id==renovación.Id));
            Assert.NotNull(db.BuscarCobertura("AF222BB","Auto",DateTimeOffset.UtcNow));
            Assert.Null(db.BuscarCobertura("AF222BB","Auto",original.FinUtc.Value.AddMonths(2)));
            Assert.Equal(4_000_000,db.ObtenerResumenCaja()!.CobrosEfectivoCentavos);
        }
        finally{Limpiar(dir);}
    }

    [Fact]
    public void Lavado_NoPuedeDevolverseDosVeces_YAfectaCajaEInforme()
    {
        var (db,dir)=Crear();
        try
        {
            db.AbrirTurno("Caja",300_000);
            db.ConfigurarPinSupervisorInicial("123456");
            var servicio=db.GuardarServicioLavado("Completo","Auto",150_000);
            var orden=db.CrearOrdenLavado(servicio.Id,"AA123BC","Lavador","");
            var pago=db.CobrarComplemento("Lavadero",orden.Id,MediosPago.Efectivo,
                200_000,"",false,Guid.NewGuid());
            Assert.Equal(450_000,db.ObtenerTurnoAbierto()!.EsperadoCentavos);
            Assert.Throws<InvalidOperationException>(()=>db.DevolverComplementoEfectivo(
                pago.Id,"Servicio cancelado","Supervisor","111111"));
            var devolución=db.DevolverComplementoEfectivo(
                pago.Id,"Servicio cancelado","Supervisor","123456");
            Assert.Equal(150_000,devolución.ImporteCentavos);
            Assert.Equal(300_000,db.ObtenerTurnoAbierto()!.EsperadoCentavos);
            Assert.Equal(EstadosLavado.Cancelado,db.ListarOrdenesLavado().Single().Estado);
            Assert.Throws<InvalidOperationException>(()=>db.DevolverComplementoEfectivo(
                pago.Id,"Servicio cancelado","Supervisor","123456"));
            var reporte=db.ReporteRecaudacion(DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            Assert.Equal(0,reporte.CobrosNetosCentavos);
            Assert.Equal(150_000,reporte.DevolucionesCentavos);
            Assert.Equal(0,reporte.EfectivoCentavos);
            Assert.Equal(2,reporte.Lineas.Count(x=>x.Origen=="Lavadero"));
            Assert.True(db.VerificarCadenaAuditoria());
        }
        finally{Limpiar(dir);}
    }

    [Fact]
    public void AbonoUsadoNoSeDevuelve_ElectronicoNoSeDevuelveComoEfectivo()
    {
        var (db,dir)=Crear();
        try
        {
            db.AbrirTurno("Caja",3_000_000);
            db.ConfigurarPinSupervisorInicial("123456");
            var plan=db.GuardarPlanAbono("Mensual","Auto","Mensual",1_000_000);
            var contrato=db.CrearContratoPendiente("Empresa","AB111AB",plan.Id);
            var pago=db.CobrarComplemento("Abono",contrato.Id,MediosPago.Efectivo,
                1_000_000,"",false,Guid.NewGuid());
            db.RegistrarIngreso("AB111AB","Auto","General");
            Assert.Throws<InvalidOperationException>(()=>db.DevolverComplementoEfectivo(
                pago.Id,"Cancelación por error","Supervisor","123456"));
            var otro=db.CrearContratoPendiente("Empresa","BB222CC",plan.Id);
            var externo=db.CobrarComplemento("Abono",otro.Id,MediosPago.Transferencia,
                1_000_000,"OPERACION99",true,Guid.NewGuid());
            Assert.Throws<InvalidOperationException>(()=>db.DevolverComplementoEfectivo(
                externo.Id,"Cancelación por error","Supervisor","123456"));
        }
        finally{Limpiar(dir);}
    }
}
