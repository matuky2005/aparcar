using System;
using System.Globalization;
using System.Linq;
using Aparcar.Domain;

namespace Aparcar.Infrastructure;

/// <summary>
/// Regla de UX para receptor sin identificar, no reemplaza las validaciones
/// tributarias actuales ni determina el tipo A/B/C o el IVA por sí sola.
/// </summary>
public sealed record ReceptorConsumidorFinalDefault(int DocTipo,long DocNumero,int CondicionIvaId);

public static class ReglasConsumidorFinalV17
{
    // Límite RG 5866/2026: revisión obligatoria ante cambios normativos.
    public const long LimiteIdentificacionObligatoriaCentavos=1_000_000_000L;

    public static ReceptorConsumidorFinalDefault? Resolver(
        int tipoComprobante,long totalCentavos,
        System.Collections.Generic.IReadOnlyList<CondicionIvaReceptorWsfe> condiciones,
        bool requiereIdentificacion=false)
    {
        ArgumentNullException.ThrowIfNull(condiciones);
        if(requiereIdentificacion || tipoComprobante is not (6 or 11) ||
           totalCentavos<=0 ||
           totalCentavos>=LimiteIdentificacionObligatoriaCentavos)
            return null;
        var clase=tipoComprobante==6?"B":"C";
        var candidatos=condiciones.Where(x=>x.Clase==clase &&
            x.Id>0 && x.Descripcion.Contains("CONSUMIDOR FINAL",
                StringComparison.OrdinalIgnoreCase)).ToArray();
        // No adivinar el código de IVA: exige una única opción oficial.
        if(candidatos.Length!=1)return null;
        return new ReceptorConsumidorFinalDefault(99,0,candidatos[0].Id);
    }

    public static void ValidarIdentificacion(ComprobanteFiscalWsfe c)
    {
        if(c.DocTipo==99 &&
           (c.Tipo is not (6 or 11) ||
            c.DocNumero!=0 ||
            c.Total*100m>=LimiteIdentificacionObligatoriaCentavos))
            throw new InvalidOperationException(
                "Consumidor Final sin identificar no corresponde a esta clase, documento o importe. Identificá al receptor.");
    }
}

public sealed partial class AparcarDb
{
    private static string ClaveCf(string cuit)
    {
        if(!AdaptadorWsfeFacturador.CuitValido(cuit))
            throw new ArgumentException("CUIT emisor inválido.");
        return "Fiscal.CFPorDefecto."+cuit;
    }

    public bool ConsumidorFinalPredeterminado(string cuit)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT Valor FROM PreferenciasDispositivos WHERE Clave=$clave";
        cmd.Parameters.AddWithValue("$clave",ClaveCf(cuit));
        return string.Equals(cmd.ExecuteScalar() as string,"true",StringComparison.Ordinal);
    }

    public void ConfigurarConsumidorFinalPredeterminado(string cuit,bool valor)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO PreferenciasDispositivos(Clave,Valor)
                VALUES($clave,$valor)
                ON CONFLICT(Clave) DO UPDATE SET Valor=excluded.Valor;
                """;
            cmd.Parameters.AddWithValue("$clave",ClaveCf(cuit));
            cmd.Parameters.AddWithValue("$valor",valor?"true":"false");
            cmd.ExecuteNonQuery();
        }
        AuditarSesion(db,tx,"Fiscal.PredeterminarConsumidorFinal",cuit,
            valor?"Activado":"Desactivado");
        tx.Commit();
    }
}
