using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

/// <summary>
/// Datos fiscales de un receptor identificable. Cada emisor mantiene su propia cartera.
/// Versiones históricas conservadas: cambiar un cliente NO cambia una factura pasada.
/// No se guardan consumidores finales anónimos, certificados ni respuestas SOAP íntegras.
/// </summary>
public sealed record ClienteFiscalGuardado(
    Guid Id,string CuitEmisor,ReceptorDocumentoFiscal Receptor,
    int Version,bool Activo,string OrigenDatos,
    DateTimeOffset? ConstanciaConsultadaUtc,DateTimeOffset ModificadoUtc);

public sealed partial class AparcarDb
{
    private void InicializarV18Clientes()
    {
        using(var db=Abrir())
        using(var consulta=db.CreateCommand())
        {
            consulta.CommandText="SELECT COUNT(*) FROM EsquemaVersion WHERE Numero=15";
            if(Convert.ToInt64(consulta.ExecuteScalar(),CultureInfo.InvariantCulture)>0)return;
        }
        CrearRespaldoInterno();
        using var cn=Abrir();
        using var tx=cn.BeginTransaction();
        using var cmd=cn.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS ClientesFiscales(
                Id TEXT PRIMARY KEY,
                CuitEmisor TEXT NOT NULL,
                DocTipo INTEGER NOT NULL CHECK(DocTipo IN(80,86,96)),
                DocNumero INTEGER NOT NULL CHECK(DocNumero>0),
                NombreBusqueda TEXT NOT NULL,
                VersionActual INTEGER NOT NULL CHECK(VersionActual>0),
                Activo INTEGER NOT NULL CHECK(Activo IN (0,1)),
                CreadoUtc TEXT NOT NULL,
                ActualizadoUtc TEXT NOT NULL,
                UNIQUE(CuitEmisor,DocTipo,DocNumero)
            );
            CREATE TABLE IF NOT EXISTS VersionesClientesFiscales(
                ClienteId TEXT NOT NULL REFERENCES ClientesFiscales(Id),
                Version INTEGER NOT NULL CHECK(Version>0),
                DatosJson TEXT NOT NULL,
                HuellaSha256 TEXT NOT NULL,
                OrigenDatos TEXT NOT NULL CHECK(OrigenDatos IN ('Manual','ConstanciaHomologacion')),
                ConstanciaConsultadaUtc TEXT,
                RegistradoUtc TEXT NOT NULL,
                PRIMARY KEY(ClienteId,Version)
            );
            CREATE INDEX IF NOT EXISTS IX_ClientesFiscales_Emisor
                ON ClientesFiscales(CuitEmisor,Activo,DocNumero);
            CREATE INDEX IF NOT EXISTS IX_ClientesFiscales_Nombre
                ON ClientesFiscales(CuitEmisor,Activo,NombreBusqueda);
            INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                VALUES(15,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private static string NormalizarBusquedaCliente(string valor)
    {
        var texto=valor.Normalize(NormalizationForm.FormD);
        var salida=new StringBuilder(texto.Length);
        foreach(var ch in texto)
        {
            if(CharUnicodeInfo.GetUnicodeCategory(ch)!=UnicodeCategory.NonSpacingMark)
                salida.Append(char.ToLowerInvariant(ch));
        }
        return salida.ToString().Normalize(NormalizationForm.FormC);
    }

    public static void ValidarClienteFiscal(ReceptorDocumentoFiscal r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if(r.DocumentoTipo is not (80 or 86 or 96) || r.DocumentoNumero<=0 ||
            (r.DocumentoTipo==96 && r.DocumentoNumero>999999999L) ||
            (r.DocumentoTipo is 80 or 86 &&
              !AdaptadorWsfeFacturador.CuitValido(
                r.DocumentoNumero.ToString("00000000000",CultureInfo.InvariantCulture))))
            throw new ArgumentException("Para guardar un cliente identificá un CUIT, CUIL o DNI válido.");
        static void Chequear(string? valor,string campo,int max,bool requerido=true)
        {
            if(valor is null ||
               requerido && string.IsNullOrWhiteSpace(valor) ||
               valor.Length>max || valor.Any(char.IsControl))
                throw new ArgumentException($"Campo de cliente inválido: {campo}.");
        }
        Chequear(r.Nombre,"nombre/razón social",180);
        Chequear(r.Domicilio,"domicilio",250);
        if(r.Correo is not null)
        {
            Chequear(r.Correo,"correo",200,false);
            if(r.Correo.Length>0 && (r.Correo.Count(x=>x=='@')!=1 ||
                r.Correo.Any(char.IsWhiteSpace)))
                throw new ArgumentException("Correo de cliente inválido.");
        }
        if(r.CondicionIvaId<0)
            throw new ArgumentException("El identificador IVA no puede ser negativo.");
        // ID 0 significa "pendiente de selección". Nunca autoriza una factura.
    }

    private static ReceptorDocumentoFiscal LeerReceptor(string json,string hash,
        int documentoTipo,long documentoNumero)
    {
        if(!string.Equals(Huella(json),hash,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Los datos del cliente fueron modificados fuera de Aparcar.");
        var receptor=JsonSerializer.Deserialize<ReceptorDocumentoFiscal>(json) ??
            throw new InvalidOperationException("No se pudo recuperar el cliente.");
        ValidarClienteFiscal(receptor);
        if(receptor.DocumentoTipo!=documentoTipo || receptor.DocumentoNumero!=documentoNumero)
            throw new InvalidOperationException("El documento del cliente no coincide con la clave del registro.");
        return receptor;
    }

    private static ClienteFiscalGuardado LeerCliente(SqliteDataReader r)
    {
        var id=Guid.Parse(r.GetString(0));
        var cuit=r.GetString(1);
        if(!AdaptadorWsfeFacturador.CuitValido(cuit))
            throw new InvalidOperationException("CUIT emisor de la cartera inválido.");
        var tipo=r.GetInt32(2);
        var numero=r.GetInt64(3);
        var receptor=LeerReceptor(r.GetString(6),r.GetString(7),tipo,numero);
        var origen=r.GetString(8);
        var consultada=r.IsDBNull(9)?(DateTimeOffset?)null:
            DateTimeOffset.Parse(r.GetString(9),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
        return new ClienteFiscalGuardado(id,cuit,receptor,r.GetInt32(4),r.GetInt32(5)==1,
            origen,consultada,DateTimeOffset.Parse(r.GetString(10),
                CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind));
    }

    private const string ColumnasClienteFiscal="""
        c.Id,c.CuitEmisor,c.DocTipo,c.DocNumero,v.Version,c.Activo,
        v.DatosJson,v.HuellaSha256,v.OrigenDatos,v.ConstanciaConsultadaUtc,
        v.RegistradoUtc
        """;

    public ClienteFiscalGuardado GuardarClienteFiscal(
        string cuitEmisor,ReceptorDocumentoFiscal receptor,
        string origenDatos="Manual",DateTimeOffset? constanciaConsultadaUtc=null)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        if(!AdaptadorWsfeFacturador.CuitValido(cuitEmisor))
            throw new ArgumentException("CUIT emisor inválido.");
        ValidarClienteFiscal(receptor);
        if(origenDatos is not ("Manual" or "ConstanciaHomologacion") ||
           (origenDatos=="ConstanciaHomologacion" &&
             (receptor.DocumentoTipo!=80 || constanciaConsultadaUtc is null ||
              constanciaConsultadaUtc>DateTimeOffset.UtcNow)) ||
           (origenDatos=="Manual" && constanciaConsultadaUtc is not null))
            throw new ArgumentException("Origen de datos o fecha de constancia inválidos.");

        var json=JsonSerializer.Serialize(receptor);
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        Guid id=Guid.NewGuid();
        int version=0;
        bool activo=false;
        string? previo=null;
        using(var buscar=db.CreateCommand())
        {
            buscar.Transaction=tx;
            buscar.CommandText="""
                SELECT c.Id,c.VersionActual,c.Activo,v.DatosJson,v.HuellaSha256
                FROM ClientesFiscales c
                INNER JOIN VersionesClientesFiscales v
                   ON v.ClienteId=c.Id AND v.Version=c.VersionActual
                WHERE c.CuitEmisor=$cuit AND c.DocTipo=$tipo AND c.DocNumero=$numero;
                """;
            buscar.Parameters.AddWithValue("$cuit",cuitEmisor);
            buscar.Parameters.AddWithValue("$tipo",receptor.DocumentoTipo);
            buscar.Parameters.AddWithValue("$numero",receptor.DocumentoNumero);
            using var r=buscar.ExecuteReader();
            if(r.Read())
            {
                id=Guid.Parse(r.GetString(0));
                version=r.GetInt32(1);
                activo=r.GetInt32(2)==1;
                previo=r.GetString(3);
                LeerReceptor(previo,r.GetString(4),receptor.DocumentoTipo,receptor.DocumentoNumero);
            }
        }
        if(previo==json && activo)
        {
            tx.Commit();
            return ObtenerClienteFiscal(id) ??
                throw new InvalidOperationException("No se pudo recuperar el cliente guardado.");
        }

        var ahora=DateTimeOffset.UtcNow.ToString("O");
        if(version==0)
        {
            using var insertar=db.CreateCommand();
            insertar.Transaction=tx;
            insertar.CommandText="""
                INSERT INTO ClientesFiscales
                    (Id,CuitEmisor,DocTipo,DocNumero,NombreBusqueda,VersionActual,Activo,CreadoUtc,ActualizadoUtc)
                VALUES($id,$cuit,$tipo,$numero,$nombre,1,1,$fecha,$fecha);
                """;
            insertar.Parameters.AddWithValue("$id",id.ToString("D"));
            insertar.Parameters.AddWithValue("$cuit",cuitEmisor);
            insertar.Parameters.AddWithValue("$tipo",receptor.DocumentoTipo);
            insertar.Parameters.AddWithValue("$numero",receptor.DocumentoNumero);
            insertar.Parameters.AddWithValue("$nombre",NormalizarBusquedaCliente(receptor.Nombre.Trim()));
            insertar.Parameters.AddWithValue("$fecha",ahora);
            insertar.ExecuteNonQuery();
        }
        int nuevaVersion=previo==json?version:checked(version+1);
        if(nuevaVersion>version)
        {
            using var insertar=db.CreateCommand();
            insertar.Transaction=tx;
            insertar.CommandText="""
                INSERT INTO VersionesClientesFiscales(
                    ClienteId,Version,DatosJson,HuellaSha256,OrigenDatos,
                    ConstanciaConsultadaUtc,RegistradoUtc)
                VALUES($id,$version,$json,$hash,$origen,$consulta,$fecha);
                """;
            insertar.Parameters.AddWithValue("$id",id.ToString("D"));
            insertar.Parameters.AddWithValue("$version",nuevaVersion);
            insertar.Parameters.AddWithValue("$json",json);
            insertar.Parameters.AddWithValue("$hash",Huella(json));
            insertar.Parameters.AddWithValue("$origen",origenDatos);
            insertar.Parameters.AddWithValue("$consulta",constanciaConsultadaUtc is null?
                (object)DBNull.Value:constanciaConsultadaUtc.Value.ToUniversalTime().ToString("O"));
            insertar.Parameters.AddWithValue("$fecha",ahora);
            insertar.ExecuteNonQuery();
        }
        if(version>0)
        {
            using var actualizar=db.CreateCommand();
            actualizar.Transaction=tx;
            actualizar.CommandText="""
                UPDATE ClientesFiscales SET VersionActual=$version,Activo=1,
                   NombreBusqueda=$nombre,ActualizadoUtc=$fecha
                WHERE Id=$id;
                """;
            actualizar.Parameters.AddWithValue("$version",nuevaVersion);
            actualizar.Parameters.AddWithValue("$nombre",NormalizarBusquedaCliente(receptor.Nombre.Trim()));
            actualizar.Parameters.AddWithValue("$fecha",ahora);
            actualizar.Parameters.AddWithValue("$id",id.ToString("D"));
            if(actualizar.ExecuteNonQuery()!=1)
                throw new InvalidOperationException("No se pudo actualizar el cliente.");
        }
        AuditarSesion(db,tx,"Fiscal.ClienteGuardado",id.ToString("D"),
            $"Versión {nuevaVersion}; no se registran datos identificatorios del cliente en auditoría");
        tx.Commit();
        return ObtenerClienteFiscal(id) ??
            throw new InvalidOperationException("No se pudo recuperar el cliente.");
    }

    public ClienteFiscalGuardado? ObtenerClienteFiscal(Guid id,int? version=null)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        if(id==Guid.Empty||version is <1)throw new ArgumentException("Identificador o versión inválidos.");
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText=$"""
            SELECT {ColumnasClienteFiscal}
            FROM ClientesFiscales c
            INNER JOIN VersionesClientesFiscales v ON v.ClienteId=c.Id
            WHERE c.Id=$id AND v.Version=COALESCE($version,c.VersionActual);
            """;
        cmd.Parameters.AddWithValue("$id",id.ToString("D"));
        cmd.Parameters.AddWithValue("$version",version is null?DBNull.Value:(object)version.Value);
        using var reader=cmd.ExecuteReader();
        return reader.Read()?LeerCliente(reader):null;
    }

    public IReadOnlyList<ClienteFiscalGuardado> BuscarClientesFiscales(
        string cuitEmisor,string? consulta=null,int max=100)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        if(!AdaptadorWsfeFacturador.CuitValido(cuitEmisor) || max is <1 or >500)
            throw new ArgumentException("CUIT emisor o límite de búsqueda inválido.");
        var filtro=(consulta??"").Trim();
        if(filtro.Length>100)throw new ArgumentException("La búsqueda es demasiado larga.");
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText=$"""
            SELECT {ColumnasClienteFiscal}
            FROM ClientesFiscales c
            INNER JOIN VersionesClientesFiscales v
                 ON v.ClienteId=c.Id AND v.Version=c.VersionActual
            WHERE c.CuitEmisor=$cuit AND c.Activo=1
              AND ($busca='' OR CAST(c.DocNumero AS TEXT) LIKE $documento
                   OR c.NombreBusqueda LIKE $nombre ESCAPE '\')
            ORDER BY c.ActualizadoUtc DESC LIMIT $max;
            """;
        cmd.Parameters.AddWithValue("$cuit",cuitEmisor);
        cmd.Parameters.AddWithValue("$busca",filtro);
        cmd.Parameters.AddWithValue("$documento",filtro+"%");
        string escapar=NormalizarBusquedaCliente(filtro)
            .Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_");
        cmd.Parameters.AddWithValue("$nombre","%"+escapar+"%");
        cmd.Parameters.AddWithValue("$max",max);
        using var r=cmd.ExecuteReader();
        var lista=new List<ClienteFiscalGuardado>();
        while(r.Read())lista.Add(LeerCliente(r));
        return lista;
    }

    public IReadOnlyList<ClienteFiscalGuardado> ListarVersionesClienteFiscal(
        Guid id,int max=100)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        if(id==Guid.Empty || max is <1 or >500)
            throw new ArgumentException("Cliente o límite inválidos.");
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText=$"""
            SELECT {ColumnasClienteFiscal}
            FROM ClientesFiscales c
            INNER JOIN VersionesClientesFiscales v ON v.ClienteId=c.Id
            WHERE c.Id=$id
            ORDER BY v.Version DESC LIMIT $max;
            """;
        cmd.Parameters.AddWithValue("$id",id.ToString("D"));
        cmd.Parameters.AddWithValue("$max",max);
        using var r=cmd.ExecuteReader();
        var lista=new List<ClienteFiscalGuardado>();
        while(r.Read())lista.Add(LeerCliente(r));
        return lista;
    }

    public void DesactivarClienteFiscal(Guid id)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            UPDATE ClientesFiscales SET Activo=0,ActualizadoUtc=$fecha
            WHERE Id=$id AND Activo=1;
            """;
        cmd.Parameters.AddWithValue("$id",id.ToString("D"));
        cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
        if(cmd.ExecuteNonQuery()!=1)
            throw new InvalidOperationException("Cliente inexistente o ya desactivado.");
        AuditarSesion(db,tx,"Fiscal.ClienteDesactivado",id.ToString("D"),
            "Se oculta de las búsquedas sin modificar comprobantes anteriores.");
        tx.Commit();
    }

    private static void VerificarVersionClienteFiscal(
        SqliteConnection db,SqliteTransaction tx,DatosDocumentoFiscalPrueba datos,
        string cuitEmisor)
    {
        if(datos.ClienteFiscalId is null && datos.VersionClienteFiscal is null)return;
        if(datos.ClienteFiscalId is not Guid id || id==Guid.Empty ||
            datos.VersionClienteFiscal is not int version || version<1)
            throw new InvalidOperationException("Referencia histórica de cliente incompleta.");
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            SELECT c.CuitEmisor,c.DocTipo,c.DocNumero,v.DatosJson,v.HuellaSha256
            FROM ClientesFiscales c
            INNER JOIN VersionesClientesFiscales v ON v.ClienteId=c.Id
            WHERE c.Id=$id AND v.Version=$version;
            """;
        cmd.Parameters.AddWithValue("$id",id.ToString("D"));
        cmd.Parameters.AddWithValue("$version",version);
        using var r=cmd.ExecuteReader();
        if(!r.Read())
            throw new InvalidOperationException("La versión del cliente seleccionado no existe.");
        if(r.GetString(0)!=cuitEmisor)
            throw new InvalidOperationException("El cliente pertenece a un CUIT emisor distinto.");
        var json=r.GetString(3);
        var receptor=LeerReceptor(json,r.GetString(4),r.GetInt32(1),r.GetInt64(2));
        if(receptor!=datos.Receptor)
            throw new InvalidOperationException(
                "El receptor fue editado después de aplicarlo. Guardá una nueva versión o volvé a aplicarlo.");
    }
}
