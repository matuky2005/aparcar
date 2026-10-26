using System;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class ClientesFiscalesV18Tests
{
    private static (AparcarDb Db,string Carpeta) NuevaDb()
    {
        var carpeta=Path.Combine(Path.GetTempPath(),"AparcarClientes18",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(carpeta);
        var db=new AparcarDb(Path.Combine(carpeta,"aparcar.db"));
        db.Inicializar();
        db.CrearAdministradorInicial("AdminClientes","ClaveSegura2026");
        db.IniciarSesion("AdminClientes","ClaveSegura2026");
        return(db,carpeta);
    }

    private static void Limpiar(string carpeta)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(carpeta,true);
    }

    private static ReceptorDocumentoFiscal Receptor(string nombre="Cliente Prueba",
        string domicilio="Calle 123",int iva=5,long numero=12345678)=>
        new(nombre,domicilio,96,numero,iva,"cliente@example.test");

    private static EmisorDocumentoFiscal Emisor()=>
        new("Aparcar Prueba SRL","Aparcar","20123456786",
            "Calle 100","IVA Responsable Inscripto","123-456",new DateOnly(2020,1,1));

    private static SolicitudFiscalPrueba Solicitud(AparcarDb db)
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
        return db.PrepararFacturaHomologacion(
            "20123456786","Lavadero",cobro.Id,comprobante);
    }

    private static DatosDocumentoFiscalPrueba Datos(SolicitudFiscalPrueba s,
        ClienteFiscalGuardado cliente)=>
        new(Emisor(),cliente.Receptor,"Servicio de lavado","Contado",
            null,cliente.Id,cliente.Version);

    [Fact]
    public void ClienteUnicoPorEmisorYDocumento_YVersionesHistoricas()
    {
        var(db,dir)=NuevaDb();
        try
        {
            var v1=db.GuardarClienteFiscal("20123456786",Receptor());
            var repetido=db.GuardarClienteFiscal("20123456786",Receptor());
            Assert.Equal(v1.Id,repetido.Id);
            Assert.Equal(1,repetido.Version);
            var v2=db.GuardarClienteFiscal("20123456786",Receptor(domicilio:"Calle 999"));
            Assert.Equal(v1.Id,v2.Id);
            Assert.Equal(2,v2.Version);
            Assert.Equal("Calle 123",db.ObtenerClienteFiscal(v1.Id,1)!.Receptor.Domicilio);
            Assert.Equal("Calle 999",db.ObtenerClienteFiscal(v1.Id)!.Receptor.Domicilio);
            Assert.Equal(2,db.ListarVersionesClienteFiscal(v1.Id).Count);
            Assert.Single(db.BuscarClientesFiscales("20123456786","Cliente"));
            Assert.Single(db.BuscarClientesFiscales("20123456786","12345678"));
            Assert.Empty(db.BuscarClientesFiscales("20123456786","No existe"));
            var otro=db.GuardarClienteFiscal("30000000007",Receptor());
            Assert.NotEqual(v1.Id,otro.Id);
            Assert.Empty(db.BuscarClientesFiscales("30000000007","Cliente extraño"));
            Assert.Single(db.BuscarClientesFiscales("30000000007"));
        }
        finally {Limpiar(dir);}
    }

    [Fact]
    public void HistoricoFiscalInmutableAunqueClienteCambie()
    {
        var(db,dir)=NuevaDb();
        try
        {
            var v1=db.GuardarClienteFiscal("20123456786",Receptor());
            var solicitud=Solicitud(db);
            db.RegistrarDatosDocumentoFiscalPrueba(solicitud.Id,Datos(solicitud,v1));
            db.GuardarClienteFiscal("20123456786",Receptor(domicilio:"Domicilio actualizado"));
            var conservado=db.ObtenerDatosDocumentoFiscalPrueba(solicitud.Id);
            Assert.NotNull(conservado);
            Assert.Equal(v1.Id,conservado!.ClienteFiscalId);
            Assert.Equal(1,conservado.VersionClienteFiscal);
            Assert.Equal("Calle 123",conservado.Receptor.Domicilio);
            Assert.Equal("Domicilio actualizado",
                db.ObtenerClienteFiscal(v1.Id)!.Receptor.Domicilio);
        }
        finally {Limpiar(dir);}
    }

    [Fact]
    public void BloqueaVersionFalsaYReceptorEditadoSinGuardar()
    {
        var(db,dir)=NuevaDb();
        try
        {
            var guardado=db.GuardarClienteFiscal("20123456786",Receptor());
            var solicitud=Solicitud(db);
            Assert.Throws<InvalidOperationException>(()=>
                db.RegistrarDatosDocumentoFiscalPrueba(solicitud.Id,
                    Datos(solicitud,guardado) with {VersionClienteFiscal=999}));
            Assert.Throws<InvalidOperationException>(()=>
                db.RegistrarDatosDocumentoFiscalPrueba(solicitud.Id,
                    Datos(solicitud,guardado) with
                    {Receptor=guardado.Receptor with {Nombre="Nombre alterado"}}));
            Assert.Null(db.ObtenerDatosDocumentoFiscalPrueba(solicitud.Id));
        }
        finally {Limpiar(dir);}
    }

    [Fact]
    public void NoPermiteCruzarCarteraDeDistintoEmisor()
    {
        var(db,dir)=NuevaDb();
        try
        {
            var otro=db.GuardarClienteFiscal("30000000007",Receptor());
            var solicitud=Solicitud(db);
            Assert.Throws<InvalidOperationException>(()=>
                db.RegistrarDatosDocumentoFiscalPrueba(solicitud.Id,
                    Datos(solicitud,otro)));
            Assert.Null(db.ObtenerDatosDocumentoFiscalPrueba(solicitud.Id));
        }
        finally {Limpiar(dir);}
    }

    [Fact]
    public void DesactivarOcultaPeroNoDestruyeComprobantesHistoricos()
    {
        var(db,dir)=NuevaDb();
        try
        {
            var cliente=db.GuardarClienteFiscal("20123456786",Receptor());
            var solicitud=Solicitud(db);
            db.RegistrarDatosDocumentoFiscalPrueba(solicitud.Id,Datos(solicitud,cliente));
            db.DesactivarClienteFiscal(cliente.Id);
            Assert.Empty(db.BuscarClientesFiscales("20123456786"));
            Assert.False(db.ObtenerClienteFiscal(cliente.Id)!.Activo);
            Assert.Equal(1,db.ObtenerDatosDocumentoFiscalPrueba(solicitud.Id)!.VersionClienteFiscal);
            Assert.Throws<InvalidOperationException>(()=>db.DesactivarClienteFiscal(cliente.Id));
            // Un operador autorizado puede volver a guardarlo y reactivar, sin borrar versiones.
            var reactivado=db.GuardarClienteFiscal("20123456786",Receptor());
            Assert.True(reactivado.Activo);
            Assert.Equal(1,reactivado.Version);
        }
        finally {Limpiar(dir);}
    }

    [Fact]
    public void NoGuardaConsumidorFinalAnonimoNiIvaNegativo()
    {
        var(db,dir)=NuevaDb();
        try
        {
            Assert.Throws<ArgumentException>(()=>db.GuardarClienteFiscal("20123456786",
                new ReceptorDocumentoFiscal("CF","NR",99,0,5)));
            Assert.Throws<ArgumentException>(()=>db.GuardarClienteFiscal("20123456786",
                Receptor(iva:-1)));
            Assert.Empty(db.BuscarClientesFiscales("20123456786"));
        }
        finally {Limpiar(dir);}
    }

    [Fact]
    public void DetectaCorrupcionDelSnapshotDeCliente()
    {
        var(db,dir)=NuevaDb();
        try
        {
            var cliente=db.GuardarClienteFiscal("20123456786",Receptor());
            using(var cn=new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource=db.RutaDb
            }.ToString()))
            {
                cn.Open();
                using var cmd=cn.CreateCommand();
                cmd.CommandText="""
                    UPDATE VersionesClientesFiscales SET DatosJson='{}'
                    WHERE ClienteId=$id AND Version=1;
                    """;
                cmd.Parameters.AddWithValue("$id",cliente.Id.ToString("D"));
                Assert.Equal(1,cmd.ExecuteNonQuery());
            }
            Assert.Throws<InvalidOperationException>(()=>db.ObtenerClienteFiscal(cliente.Id));
            Assert.Throws<InvalidOperationException>(()=>
                db.BuscarClientesFiscales("20123456786"));
        }
        finally {Limpiar(dir);}
    }
}
