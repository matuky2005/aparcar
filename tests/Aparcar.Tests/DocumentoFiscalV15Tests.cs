using System;
using System.IO;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class DocumentoFiscalV15Tests
{
    private static (AparcarDb Db,string Carpeta) CrearDb()
    {
        var carpeta=Path.Combine(Path.GetTempPath(),"AparcarDocFiscal15",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(carpeta);
        var db=new AparcarDb(Path.Combine(carpeta,"aparcar.db"));
        db.Inicializar();
        db.CrearAdministradorInicial("AdminV15","ClaveSegura2026");
        db.IniciarSesion("AdminV15","ClaveSegura2026");
        return(db,carpeta);
    }

    private static SolicitudFiscalPrueba CrearSolicitud(AparcarDb db)
    {
        db.AbrirTurno("Cajero",0);
        var servicio=db.GuardarServicioLavado("Lavado completo","Auto",12100);
        var orden=db.CrearOrdenLavado(servicio.Id,"AA123BC","Operario","");
        var cobro=db.CobrarComplemento("Lavadero",orden.Id,MediosPago.Efectivo,
            12100,"",false,Guid.NewGuid());
        var hoy=DateOnly.FromDateTime(DateTime.Today);
        var comprobante=new ComprobanteFiscalWsfe(3,6,1,hoy,2,96,12345678,5,
            121m,0m,100m,0m,0m,21m,new[]{new IvaFiscalWsfe(5,100m,21m)},
            hoy,hoy,hoy);
        return db.PrepararFacturaHomologacion("20123456786","Lavadero",cobro.Id,comprobante);
    }

    private static DatosDocumentoFiscalPrueba Datos(SolicitudFiscalPrueba solicitud)
    {
        var emisor=new EmisorDocumentoFiscal("Estacionamiento de Prueba SRL",
            "Aparcar Prueba",solicitud.CuitEmisor,"Calle 123, Tucumán",
            "IVA Responsable Inscripto","Exento",new DateOnly(2020,1,1));
        var receptor=new ReceptorDocumentoFiscal("Cliente de prueba","Calle 321",
            solicitud.Comprobante.DocTipo,solicitud.Comprobante.DocNumero,
            solicitud.Comprobante.CondicionIvaReceptorId,"prueba@example.test");
        return new DatosDocumentoFiscalPrueba(emisor,receptor,
            "Servicio de lavado","Contado");
    }

    [Fact]
    public void CapturaSeGuardaUnaSolaVezYNoSeModificaTrasEnviar()
    {
        var(db,dir)=CrearDb();
        try
        {
            var solicitud=CrearSolicitud(db);
            var datos=Datos(solicitud);
            db.RegistrarDatosDocumentoFiscalPrueba(solicitud.Id,datos);
            Assert.Equal(datos,db.ObtenerDatosDocumentoFiscalPrueba(solicitud.Id));
            Assert.Throws<InvalidOperationException>(()=>
                db.RegistrarDatosDocumentoFiscalPrueba(solicitud.Id,datos));
            db.MarcarEnvioHomologacion(solicitud.Id);
            Assert.Throws<InvalidOperationException>(()=>
                db.RegistrarDatosDocumentoFiscalPrueba(solicitud.Id,datos));
            Assert.Equal(datos,db.ObtenerDatosDocumentoFiscalPrueba(solicitud.Id));
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(dir,true); }
    }

    [Fact]
    public void NoAceptaCuitEmisorNiReceptorDiferenteDeLaReserva()
    {
        var(db,dir)=CrearDb();
        try
        {
            var solicitud=CrearSolicitud(db);
            var datos=Datos(solicitud);
            Assert.Throws<ArgumentException>(()=>
                db.RegistrarDatosDocumentoFiscalPrueba(solicitud.Id,
                    datos with {Emisor=datos.Emisor with {Cuit="30000000007"}}));
            Assert.Throws<ArgumentException>(()=>
                db.RegistrarDatosDocumentoFiscalPrueba(solicitud.Id,
                    datos with {Receptor=datos.Receptor with {DocumentoNumero=99999999}}));
            Assert.Throws<ArgumentException>(()=>
                db.RegistrarDatosDocumentoFiscalPrueba(solicitud.Id,
                    datos with {Receptor=datos.Receptor with {CondicionIvaId=999}}));
            Assert.Null(db.ObtenerDatosDocumentoFiscalPrueba(solicitud.Id));
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(dir,true); }
    }

    [Fact]
    public void VistaAutorizadaIncluyeDatosComercialesSinQrFiscal()
    {
        var(db,dir)=CrearDb();
        try
        {
            var solicitud=CrearSolicitud(db);
            db.RegistrarDatosDocumentoFiscalPrueba(solicitud.Id,Datos(solicitud));
            db.MarcarEnvioHomologacion(solicitud.Id);
            var autorizado=db.RegistrarRespuestaHomologacion(solicitud.Id,
                new ResultadoAutorizacionWsfe(EstadoAutorizacionWsfe.Autorizado,
                    "71234567890123",new DateOnly(2026,11,5),
                    Array.Empty<int>(),"CAE de prueba"));
            var vista=PresentacionFiscalHomologacion.Crear(
                autorizado,db.ObtenerDatosDocumentoFiscalPrueba(solicitud.Id));
            var texto=string.Join("\n",vista.Lineas);
            Assert.Contains("Estacionamiento de Prueba SRL",texto);
            Assert.Contains("Servicio de lavado",texto);
            Assert.Contains("PRUEBA SIN VALIDEZ FISCAL",texto);
            Assert.DoesNotContain("https://www.arca.gob.ar/fe/qr/",texto);
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(dir,true); }
    }

    [Fact]
    public void IntegridadSnapshotSeCompruebaAntesDeMostrar()
    {
        var(db,dir)=CrearDb();
        try
        {
            var solicitud=CrearSolicitud(db);
            db.RegistrarDatosDocumentoFiscalPrueba(solicitud.Id,Datos(solicitud));
            using(var cn=new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource=db.RutaDb
            }.ToString()))
            {
                cn.Open();
                using var cmd=cn.CreateCommand();
                cmd.CommandText="UPDATE DatosDocumentosFiscalesPrueba SET DatosJson='{}' WHERE SolicitudId=$id";
                cmd.Parameters.AddWithValue("$id",solicitud.Id.ToString("D"));
                Assert.Equal(1,cmd.ExecuteNonQuery());
            }
            Assert.Throws<InvalidOperationException>(()=>
                db.ObtenerDatosDocumentoFiscalPrueba(solicitud.Id));
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(dir,true); }
    }
}
