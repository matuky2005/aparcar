using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

/// <summary>Catálogo local: no crea puntos de venta ni reserva números en ARCA.</summary>
public sealed record TalonarioFiscal(string Ambiente,string Cuit,int PuntoVenta,int Tipo,
    string Nombre,bool Habilitado);

public sealed partial class AparcarDb
{
    private void InicializarV25Talonarios()
    {
        using(var cn=Abrir())
        using(var consulta=cn.CreateCommand())
        {
            consulta.CommandText="SELECT COUNT(*) FROM EsquemaVersion WHERE Numero=19";
            if(Convert.ToInt64(consulta.ExecuteScalar(),CultureInfo.InvariantCulture)>0)return;
        }
        CrearRespaldoInterno();
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS TalonariosFiscales(
                Ambiente TEXT NOT NULL CHECK(Ambiente IN ('Homologacion','Produccion')),
                Cuit TEXT NOT NULL,
                PuntoVenta INTEGER NOT NULL CHECK(PuntoVenta BETWEEN 1 AND 99998),
                Tipo INTEGER NOT NULL CHECK(Tipo IN (1,6,11)),
                Nombre TEXT NOT NULL,
                Habilitado INTEGER NOT NULL CHECK(Habilitado IN (0,1)),
                PRIMARY KEY(Ambiente,Cuit,PuntoVenta,Tipo)
            );
            INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                VALUES(19,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public TalonarioX ActualizarTalonarioX(string codigo,string nombre,bool habilitado)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        codigo=NormalizarCodigoX(codigo);
        nombre=(nombre??"").Trim();
        if(nombre.Length is <3 or >80 || nombre.Any(char.IsControl))
            throw new ArgumentException("Nombre de talonario inválido.");
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            UPDATE TalonariosX SET Nombre=$nombre,Habilitado=$habilitado
            WHERE Codigo=$codigo RETURNING UltimoNumero;
            """;
        cmd.Parameters.AddWithValue("$nombre",nombre);
        cmd.Parameters.AddWithValue("$habilitado",habilitado?1:0);
        cmd.Parameters.AddWithValue("$codigo",codigo);
        var valor=cmd.ExecuteScalar();
        if(valor is null)throw new InvalidOperationException("El talonario no existe.");
        var numero=Convert.ToInt64(valor,CultureInfo.InvariantCulture);
        AuditarSesion(db,tx,"TicketX.Modificar",codigo,$"Nombre={nombre}; habilitado={habilitado}");
        tx.Commit();
        return new TalonarioX(codigo,nombre,numero,habilitado);
    }

    public void GuardarTalonarioFiscal(TalonarioFiscal talonario)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        ArgumentNullException.ThrowIfNull(talonario);
        var nombre=(talonario.Nombre??"").Trim();
        if(talonario.Ambiente is not ("Homologacion" or "Produccion") ||
           !AdaptadorWsfeFacturador.CuitValido(talonario.Cuit) ||
           talonario.PuntoVenta is <1 or >99998 || talonario.Tipo is not (1 or 6 or 11) ||
           nombre.Length is <3 or >80 || nombre.Any(char.IsControl))
            throw new ArgumentException("Revisá ambiente, CUIT, punto de venta, clase y nombre del talonario.");
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO TalonariosFiscales(Ambiente,Cuit,PuntoVenta,Tipo,Nombre,Habilitado)
            VALUES($ambiente,$cuit,$punto,$tipo,$nombre,$habilitado)
            ON CONFLICT(Ambiente,Cuit,PuntoVenta,Tipo)
            DO UPDATE SET Nombre=excluded.Nombre,Habilitado=excluded.Habilitado;
            """;
        cmd.Parameters.AddWithValue("$ambiente",talonario.Ambiente);
        cmd.Parameters.AddWithValue("$cuit",talonario.Cuit);
        cmd.Parameters.AddWithValue("$punto",talonario.PuntoVenta);
        cmd.Parameters.AddWithValue("$tipo",talonario.Tipo);
        cmd.Parameters.AddWithValue("$nombre",nombre);
        cmd.Parameters.AddWithValue("$habilitado",talonario.Habilitado?1:0);
        cmd.ExecuteNonQuery();
        AuditarSesion(db,tx,"Fiscal.Talonario",$"{talonario.Ambiente}/{talonario.Cuit}/{talonario.PuntoVenta}/{talonario.Tipo}",
            $"Nombre={nombre}; habilitado={talonario.Habilitado}; catálogo local sin asignación de números.");
        tx.Commit();
    }

    public IReadOnlyList<TalonarioFiscal> ListarTalonariosFiscales()
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT Ambiente,Cuit,PuntoVenta,Tipo,Nombre,Habilitado FROM TalonariosFiscales ORDER BY Ambiente,Cuit,PuntoVenta,Tipo";
        using var r=cmd.ExecuteReader();
        var lista=new List<TalonarioFiscal>();
        while(r.Read())lista.Add(new TalonarioFiscal(r.GetString(0),r.GetString(1),r.GetInt32(2),r.GetInt32(3),r.GetString(4),r.GetInt32(5)==1));
        return lista;
    }
}
