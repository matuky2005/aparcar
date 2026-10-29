using System;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class ModulosOperativosV6Tests
{
    private static (AparcarDb Db,string Carpeta) NuevaBase()
    {
        var carpeta=Path.Combine(Path.GetTempPath(),"AparcarV6",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(carpeta);
        var db=new AparcarDb(Path.Combine(carpeta,"aparcar.db"));
        db.Inicializar();
        db.CrearAdministradorInicial("AdminV6","ClaveSegura2026");
        db.IniciarSesion("AdminV6","ClaveSegura2026");
        return(db,carpeta);
    }
    private static void Borrar(string carpeta)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(carpeta,true);
    }
    private static void AtrasarIngreso(AparcarDb db,Guid id,int minutos)
    {
        using var sql=new SqliteConnection($"Data Source={db.RutaDb}");
        sql.Open();using var cmd=sql.CreateCommand();
        cmd.CommandText="UPDATE Estadias SET IngresoUtc=$fecha WHERE Id=$id";
        cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.AddMinutes(-minutos).ToString("O"));
        cmd.Parameters.AddWithValue("$id",id.ToString("D"));
        Assert.Equal(1,cmd.ExecuteNonQuery());
    }

    [Fact]
    public void Lavadero_EvitaDuplicados_CongelaPrecio_YBloqueaSalidaSiEstaPendiente()
    {
        var (db,carpeta)=NuevaBase();
        try
        {
            db.AbrirTurno("CajeroPrueba",100000);
            var auto=db.RegistrarIngreso("AF123BC","Auto","General");
            var estadia=db.BuscarActiva(auto.ToString())!;
            var version1=db.GuardarServicioLavado("Completo","Auto",240000);
            var orden=db.CrearOrdenLavado(version1.Id,"AF123BC","Operario 1","Sin marcas",estadia.Id);
            Assert.Equal(240000,orden.PrecioCentavos);
            var version2=db.GuardarServicioLavado("Completo","Auto",300000);
            Assert.Equal(2,version2.Version);
            Assert.False(db.ListarServiciosLavado().Any(x=>x.Id==version1.Id));
            Assert.Equal(240000,db.ListarOrdenesLavado().Single().PrecioCentavos);

            var cotizacion=db.CotizarSalida(estadia.Id);
            Assert.Throws<InvalidOperationException>(()=>db.ConfirmarPago(
                cotizacion,MediosPago.Efectivo,cotizacion.ImporteCentavos,"",false,Guid.NewGuid()));
            Assert.Throws<InvalidOperationException>(()=>
                db.CambiarEstadoOrdenLavado(orden.Id,EstadosLavado.Entregado));
            Assert.Throws<InvalidOperationException>(()=>
                db.CambiarEstadoOrdenLavado(orden.Id,EstadosLavado.Terminado));

            var enProceso=db.CambiarEstadoOrdenLavado(orden.Id,EstadosLavado.EnProceso);
            Assert.Equal(EstadosLavado.EnProceso,enProceso.Estado);
            var clave=Guid.NewGuid();
            var cobro=db.CobrarComplemento("Lavadero",orden.Id,MediosPago.Efectivo,
                300000,"",false,clave);
            Assert.Equal(240000,cobro.ImporteCentavos);
            Assert.Equal(60000,cobro.VueltoCentavos);
            Assert.Equal(cobro.Id,db.CobrarComplemento("Lavadero",orden.Id,MediosPago.Efectivo,
                300000,"",false,clave).Id);
            Assert.Throws<InvalidOperationException>(()=>db.CobrarComplemento(
                "Lavadero",orden.Id,MediosPago.Efectivo,300000,"",false,Guid.NewGuid()));
            Assert.Equal(340000,db.ObtenerTurnoAbierto()!.EsperadoCentavos);

            db.CambiarEstadoOrdenLavado(orden.Id,EstadosLavado.Terminado);
            db.CambiarEstadoOrdenLavado(orden.Id,EstadosLavado.Entregado);
            var salida=db.ConfirmarPago(db.CotizarSalida(estadia.Id),MediosPago.Efectivo,
                0,"",false,Guid.NewGuid());
            Assert.Equal(0,salida.ImporteCentavos);
            Assert.Empty(db.ListarActivas());
            var reporte=db.ReporteRecaudacion(DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            Assert.Equal(240000,reporte.CobrosBrutosCentavos);
            Assert.Single(reporte.Lineas.Where(x=>x.Origen=="Lavadero"));
        }
        finally { Borrar(carpeta); }
    }

    [Fact]
    public void Abono_SeActivaSoloAlCobrar_NoCubreHorasPrevias_YNoSeSuperpone()
    {
        var (db,carpeta)=NuevaBase();
        try
        {
            db.AbrirTurno("CajeroPrueba",0);
            var plan1=db.GuardarPlanAbono("Mensual completo","Auto",PeriodicidadesAbono.Mensual,2500000);
            var previoTicket=db.RegistrarIngreso("AB123CD","Auto","General");
            var vieja=db.BuscarActiva(previoTicket.ToString())!;
            AtrasarIngreso(db,vieja.Id,95);
            var contrato=db.CrearContratoPendiente("Cliente Uno","AB123CD",plan1.Id);
            Assert.Null(contrato.InicioUtc);
            Assert.Equal("Pendiente",contrato.Estado);
            Assert.Null(db.BuscarCobertura("AB123CD","Auto",DateTimeOffset.UtcNow));
            Assert.Throws<InvalidOperationException>(()=>db.CobrarComplemento(
                "Abono",contrato.Id,MediosPago.Transferencia,2500000,"",false,Guid.NewGuid()));
            var pago=db.CobrarComplemento("Abono",contrato.Id,MediosPago.Transferencia,
                2500000,"TRANSFER-12345",true,Guid.NewGuid());
            Assert.Equal(2500000,pago.ImporteCentavos);
            var activo=db.ListarContratosAbono().Single();
            Assert.Equal("Activo",activo.Estado);
            Assert.NotNull(activo.FinUtc);
            Assert.Equal(activo.InicioUtc!.Value.AddMonths(1),activo.FinUtc!.Value);
            Assert.NotNull(db.BuscarCobertura("AB123CD","Auto",DateTimeOffset.UtcNow));
            Assert.Null(db.BuscarCobertura("AB123CD","Auto",DateTimeOffset.UtcNow.AddDays(-2)));
            var previo=db.CotizarSalida(vieja.Id);
            Assert.False(previo.CubiertoPorAbono);
            Assert.Equal(400000,previo.ImporteCentavos);
            db.ConfirmarPago(previo,MediosPago.Efectivo,previo.ImporteCentavos,"",false,Guid.NewGuid());
            var nuevoTicket=db.RegistrarIngreso("AB123CD","Auto","General");
            var nueva=db.BuscarActiva(nuevoTicket.ToString())!;
            Assert.True(db.CotizarSalida(nueva.Id).CubiertoPorAbono);
            Assert.Equal(0,db.CotizarSalida(nueva.Id).ImporteCentavos);
            var plan2=db.GuardarPlanAbono("Semanal","Auto",PeriodicidadesAbono.Semanal,600000);
            var segundo=db.CrearContratoPendiente("Cliente Uno","AB123CD",plan2.Id);
            Assert.Throws<InvalidOperationException>(()=>db.CobrarComplemento(
                "Abono",segundo.Id,MediosPago.Efectivo,600000,"",false,Guid.NewGuid()));
            var resumen=db.ObtenerResumenCaja()!;
            Assert.Equal(0,resumen.CobrosElectronicosCentavos-2500000);
            Assert.Equal(400000,resumen.CobrosEfectivoCentavos);
        }
        finally { Borrar(carpeta); }
    }

    [Fact]
    public void CajeroPuedeCobrarPeroNoEditarCatalogos_OperarioNoCobra()
    {
        var (db,carpeta)=NuevaBase();
        try
        {
            var servicio=db.GuardarServicioLavado("Rápido","Auto",100000);
            db.CrearUsuario("LavadorUno","ClaveLavador2026",RolesAparcar.Lavadero);
            db.CrearUsuario("CajeroDos","ClaveCajero2026",RolesAparcar.Cajero);
            db.CerrarSesion();
            db.IniciarSesion("LavadorUno","ClaveLavador2026");
            var orden=db.CrearOrdenLavado(servicio.Id,"AA001BB","Lavador Uno","");
            Assert.Throws<UnauthorizedAccessException>(()=>
                db.GuardarServicioLavado("Otro","Auto",100000));
            Assert.Throws<UnauthorizedAccessException>(()=>db.CobrarComplemento(
                "Lavadero",orden.Id,MediosPago.Efectivo,100000,"",false,Guid.NewGuid()));
            db.CerrarSesion();
            db.IniciarSesion("CajeroDos","ClaveCajero2026");
            db.AbrirTurno("Cajero Dos",0);
            Assert.Throws<UnauthorizedAccessException>(()=>
                db.GuardarPlanAbono("Mensual","Auto",PeriodicidadesAbono.Mensual,1200000));
            Assert.Equal(100000,db.CobrarComplemento("Lavadero",orden.Id,
                MediosPago.Efectivo,100000,"",false,Guid.NewGuid()).ImporteCentavos);
        }
        finally { Borrar(carpeta); }
    }
}
