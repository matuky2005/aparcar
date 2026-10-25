using System;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class ControlesV10Tests
{
    private static (AparcarDb Db,string Dir) BaseNueva()
    {
        var dir=Path.Combine(Path.GetTempPath(),"AparcarV10",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=new AparcarDb(Path.Combine(dir,"datos.db"));
        db.Inicializar();
        db.CrearAdministradorInicial("AdminV10","ClaveSegura2026");
        db.IniciarSesion("AdminV10","ClaveSegura2026");
        return(db,dir);
    }

    private static void Limpiar(string dir)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(dir,true);
    }

    private static CotizacionOperativa Cotizar(AparcarDb db,string patente,string tipo="Auto")
    {
        var ticket=db.RegistrarIngreso(patente,tipo,"General");
        var estadia=db.BuscarActiva(ticket.ToString())!;
        using(var cn=new SqliteConnection($"Data Source={db.RutaDb}"))
        {
            cn.Open();
            using var cmd=cn.CreateCommand();
            cmd.CommandText="UPDATE Estadias SET IngresoUtc=$fecha WHERE Id=$id";
            cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.AddMinutes(-80).ToString("O"));
            cmd.Parameters.AddWithValue("$id",estadia.Id.ToString("D"));
            Assert.Equal(1,cmd.ExecuteNonQuery());
        }
        return db.CotizarSalida(estadia.Id);
    }

    [Fact]
    public void CajaCiegaObligatoria_ElCajeroDebeContarAntesDeConocerElEsperado()
    {
        var(db,dir)=BaseNueva();
        try
        {
            db.ConfigurarCajaCiegaObligatoria(true);
            Assert.True(db.CajaCiegaObligatoria());
            db.CrearUsuario("CajeroDiez","ClaveCajero2026",RolesAparcar.Cajero);
            db.CerrarSesion();
            db.IniciarSesion("CajeroDiez","ClaveCajero2026");
            Assert.Throws<UnauthorizedAccessException>(()=>db.ConfigurarCajaCiegaObligatoria(false));
            var turno=db.AbrirTurno("Cajero Diez",50_000,false);
            Assert.True(db.EsTurnoCiego(turno.Id));
            Assert.Equal(0,turno.EsperadoCentavos);
            Assert.Throws<UnauthorizedAccessException>(()=>db.ObtenerResumenCaja());
            var cotizacion=Cotizar(db,"AA111AA");
            var cobro=db.ConfirmarPago(cotizacion,MediosPago.Efectivo,
                cotizacion.ImporteCentavos,"",false,Guid.NewGuid());
            Assert.Equal(300_000,cobro.ImporteCentavos);
            Assert.Equal(0,db.ObtenerTurnoAbierto()!.EsperadoCentavos);
            Assert.Throws<UnauthorizedAccessException>(()=>db.ListarAutorizacionesReimpresion());
            var cerrado=db.CerrarTurno(turno.Id,350_000);
            Assert.Equal(350_000,cerrado.EsperadoCentavos);
            Assert.Equal(0,cerrado.DiferenciaCentavos);
            Assert.Null(db.ObtenerTurnoAbierto());
            Assert.Single(db.ListarPendientesFiscales());
            Assert.Equal("PendienteDatosFiscales",db.ListarPendientesFiscales()[0].Estado);
        }
        finally{Limpiar(dir);}
    }

    [Fact]
    public void Reimpresion_ExigeSupervisorMotivoYPersisteAuditoria()
    {
        var(db,dir)=BaseNueva();
        try
        {
            var ticket=db.RegistrarIngreso("AB222AA","Auto","General");
            db.CrearUsuario("CajeroOnce","ClaveCajero2026",RolesAparcar.Cajero);
            db.CerrarSesion();
            db.IniciarSesion("CajeroOnce","ClaveCajero2026");
            Assert.Throws<UnauthorizedAccessException>(()=>
                db.AutorizarReimpresion(ticket,"Duplicado solicitado por cliente"));
            db.CerrarSesion();
            db.IniciarSesion("AdminV10","ClaveSegura2026");
            Assert.Throws<ArgumentException>(()=>db.AutorizarReimpresion(ticket,"corto"));
            Assert.Throws<InvalidOperationException>(()=>db.AutorizarReimpresion(
                ticket+900,"Duplicado por perdida de ticket"));
            var ok=db.AutorizarReimpresion(ticket,"Duplicado solicitado por cliente");
            Assert.Equal(ticket,ok.Ticket);
            Assert.Equal("AdminV10",ok.Autoriza);
            Assert.Single(db.ListarAutorizacionesReimpresion());
            Assert.True(db.VerificarCadenaAuditoria());
        }
        finally{Limpiar(dir);}
    }

    [Fact]
    public void PendientesFiscales_SonUnicosPorCobro_YNoSonFacturasAutorizadas()
    {
        var(db,dir)=BaseNueva();
        try
        {
            db.AbrirTurno("Cajero",0);
            var estadia=Cotizar(db,"CD333BB");
            var clave=Guid.NewGuid();
            var primero=db.ConfirmarPago(estadia,MediosPago.Transferencia,
                estadia.ImporteCentavos,"REF-000123",true,clave);
            var repetido=db.ConfirmarPago(estadia,MediosPago.Transferencia,
                estadia.ImporteCentavos,"REF-000123",true,clave);
            Assert.Equal(primero.Id,repetido.Id);
            var servicio=db.GuardarServicioLavado("Lavado rápido","Auto",200_000);
            var orden=db.CrearOrdenLavado(servicio.Id,"EE555EE","Operario","Sin comentarios");
            db.CobrarComplemento("Lavadero",orden.Id,MediosPago.Efectivo,200_000,
                "",false,Guid.NewGuid());

            var pendiente=db.ListarPendientesFiscales();
            Assert.Equal(2,pendiente.Count);
            Assert.Contains(pendiente,p=>p.Origen=="Estacionamiento" &&
                p.CobroId==primero.Id && p.ImporteCentavos==estadia.ImporteCentavos);
            Assert.Contains(pendiente,p=>p.Origen=="Lavadero" &&
                p.ImporteCentavos==200_000);
            Assert.All(pendiente,p=>Assert.Equal("PendienteDatosFiscales",p.Estado));
            var otra=new AparcarDb(db.RutaDb);
            otra.Inicializar();
            Assert.Throws<UnauthorizedAccessException>(()=>otra.ListarPendientesFiscales());
            otra.IniciarSesion("AdminV10","ClaveSegura2026");
            Assert.Equal(2,otra.ListarPendientesFiscales().Count);
        }
        finally{Limpiar(dir);}
    }

    [Fact]
    public void PoliticaCaja_NoSeModificaDuranteTurnoAbierto()
    {
        var(db,dir)=BaseNueva();
        try
        {
            db.ConfigurarCajaCiegaObligatoria(true);
            var turno=db.AbrirTurno("Administrador",10_000);
            Assert.True(db.EsTurnoCiego(turno.Id));
            Assert.Throws<InvalidOperationException>(()=>db.ConfigurarCajaCiegaObligatoria(false));
            Assert.Equal(10_000,db.ObtenerResumenCaja()!.EfectivoEsperadoCentavos);
            db.CerrarTurno(turno.Id,10_000);
            db.ConfigurarCajaCiegaObligatoria(false);
            Assert.False(db.CajaCiegaObligatoria());
        }
        finally{Limpiar(dir);}
    }
}
