using System;
using System.IO;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class PerfilesFiscalesV16Tests
{
    private static (AparcarDb Db,string Directorio) Crear()
    {
        var dir=Path.Combine(Path.GetTempPath(),"AparcarPerfilesV16",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=new AparcarDb(Path.Combine(dir,"aparcar.db"));
        db.Inicializar();
        db.CrearAdministradorInicial("AdminPerfil","ClaveSegura2026");
        db.IniciarSesion("AdminPerfil","ClaveSegura2026");
        return(db,dir);
    }

    private static void Limpiar(string dir)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(dir,true);
    }

    private static EmisorDocumentoFiscal Emisor(string domicilio="Calle 123") =>
        new("Estacionamiento Prueba SRL","Aparcar","20123456786",
            domicilio,"IVA Responsable Inscripto","123-456",new DateOnly(2020,1,1));

    private static SolicitudFiscalPrueba CrearSolicitud(AparcarDb db)
    {
        db.AbrirTurno("Cajero",0);
        var servicio=db.GuardarServicioLavado("Lavado completo","Auto",12100);
        var orden=db.CrearOrdenLavado(servicio.Id,"AA123BC","Operario","");
        var cobro=db.CobrarComplemento("Lavadero",orden.Id,MediosPago.Efectivo,
            12100,"",false,Guid.NewGuid());
        var hoy=DateOnly.FromDateTime(DateTime.Today);
        var comprobante=new ComprobanteFiscalWsfe(3,6,1,hoy,2,96,12345678,5,
            121m,0m,100m,0m,0m,21m,
            new[]{new IvaFiscalWsfe(5,100m,21m)},hoy,hoy,hoy);
        return db.PrepararFacturaHomologacion(Emisor().Cuit,"Lavadero",cobro.Id,comprobante);
    }

    private static DatosDocumentoFiscalPrueba Datos(
        SolicitudFiscalPrueba solicitud,EmisorDocumentoFiscal emisor,int? version) =>
        new(emisor,new ReceptorDocumentoFiscal(
            "Cliente Prueba","Calle 300",solicitud.Comprobante.DocTipo,
            solicitud.Comprobante.DocNumero,solicitud.Comprobante.CondicionIvaReceptorId),
            "Servicio de lavadero","Contado",version);

    [Fact]
    public void CambiosGeneranVersionesYSinCambiosNoDuplican()
    {
        var(db,dir)=Crear();
        try
        {
            var uno=db.GuardarPerfilFiscalEmisor(Emisor());
            Assert.Equal(1,uno.Version);
            var igual=db.GuardarPerfilFiscalEmisor(Emisor());
            Assert.Equal(1,igual.Version);
            var dos=db.GuardarPerfilFiscalEmisor(Emisor("Calle 999"));
            Assert.Equal(2,dos.Version);
            Assert.Equal("Calle 999",db.ObtenerPerfilFiscalEmisor(Emisor().Cuit)!.Emisor.DomicilioComercial);
            var anterior=db.ObtenerPerfilFiscalEmisor(Emisor().Cuit,1);
            Assert.NotNull(anterior);
            Assert.Equal("Calle 123",anterior!.Emisor.DomicilioComercial);
            Assert.False(anterior.EsVigente);
            Assert.Equal(2,db.ListarVersionesPerfilFiscal(Emisor().Cuit).Count);
            Assert.Single(db.ListarPerfilesFiscales());
        }
        finally {Limpiar(dir);}
    }

    [Fact]
    public void FacturaMantieneVersionHistoricaAunqueCambiePerfil()
    {
        var(db,dir)=Crear();
        try
        {
            var v1=db.GuardarPerfilFiscalEmisor(Emisor());
            var solicitud=CrearSolicitud(db);
            var datos=Datos(solicitud,v1.Emisor,v1.Version);
            db.RegistrarDatosDocumentoFiscalPrueba(solicitud.Id,datos);
            db.GuardarPerfilFiscalEmisor(Emisor("Nuevo domicilio comercial"));
            var conservados=db.ObtenerDatosDocumentoFiscalPrueba(solicitud.Id);
            Assert.NotNull(conservados);
            Assert.Equal(1,conservados!.VersionPerfilEmisor);
            Assert.Equal("Calle 123",conservados.Emisor.DomicilioComercial);
            Assert.Equal("Nuevo domicilio comercial",
                db.ObtenerPerfilFiscalEmisor(Emisor().Cuit)!.Emisor.DomicilioComercial);
        }
        finally {Limpiar(dir);}
    }

    [Fact]
    public void NoAceptaVersionInexistenteNiDatosEditadosDespuesDeAplicar()
    {
        var(db,dir)=Crear();
        try
        {
            var perfil=db.GuardarPerfilFiscalEmisor(Emisor());
            var solicitud=CrearSolicitud(db);
            Assert.Throws<InvalidOperationException>(()=>
                db.RegistrarDatosDocumentoFiscalPrueba(solicitud.Id,
                    Datos(solicitud,perfil.Emisor,999)));
            Assert.Throws<InvalidOperationException>(()=>
                db.RegistrarDatosDocumentoFiscalPrueba(solicitud.Id,
                    Datos(solicitud,perfil.Emisor with {DomicilioComercial="Alterado"},perfil.Version)));
            Assert.Null(db.ObtenerDatosDocumentoFiscalPrueba(solicitud.Id));
        }
        finally {Limpiar(dir);}
    }

    [Fact]
    public void NoPermiteLeerPerfilHistoricoConHashAlterado()
    {
        var(db,dir)=Crear();
        try
        {
            var perfil=db.GuardarPerfilFiscalEmisor(Emisor());
            using(var cn=new SqliteConnection(
                new SqliteConnectionStringBuilder {DataSource=db.RutaDb}.ToString()))
            {
                cn.Open();
                using var cmd=cn.CreateCommand();
                cmd.CommandText="UPDATE VersionesPerfilesFiscales SET DatosJson='{}' WHERE CuitEmisor=$cuit";
                cmd.Parameters.AddWithValue("$cuit",perfil.Cuit);
                Assert.Equal(1,cmd.ExecuteNonQuery());
            }
            Assert.Throws<InvalidOperationException>(()=>
                db.ObtenerPerfilFiscalEmisor(perfil.Cuit,1));
            Assert.Throws<InvalidOperationException>(()=>db.GuardarPerfilFiscalEmisor(Emisor()));
        }
        finally {Limpiar(dir);}
    }

    [Fact]
    public void NoAceptaPerfilConCuitInvalido()
    {
        var(db,dir)=Crear();
        try
        {
            Assert.Throws<ArgumentException>(()=>
                db.GuardarPerfilFiscalEmisor(Emisor() with {Cuit="20123456789"}));
            Assert.Empty(db.ListarPerfilesFiscales());
        }
        finally {Limpiar(dir);}
    }
}
