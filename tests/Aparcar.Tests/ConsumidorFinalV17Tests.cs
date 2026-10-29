using System;
using System.IO;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class ConsumidorFinalV17Tests
{
    private static readonly CondicionIvaReceptorWsfe[] Catalogo=
    {
        new(5,"Consumidor Final","B"),
        new(6,"Responsable Inscripto","B"),
        new(5,"Consumidor Final","C")
    };

    [Fact]
    public void SeleccionaElCodigoOficialSoloEnClaseBYC()
    {
        var b=ReglasConsumidorFinalV17.Resolver(6,12100,Catalogo);
        var c=ReglasConsumidorFinalV17.Resolver(11,12100,Catalogo);
        Assert.NotNull(b);
        Assert.NotNull(c);
        Assert.Equal(99,b!.DocTipo);
        Assert.Equal(0,b.DocNumero);
        Assert.Equal(5,c!.CondicionIvaId);
        Assert.Null(ReglasConsumidorFinalV17.Resolver(1,12100,Catalogo));
        Assert.Null(ReglasConsumidorFinalV17.Resolver(6,12100,
            new[]{new CondicionIvaReceptorWsfe(6,"Responsable Inscripto","B")}));
    }

    [Fact]
    public void NoAplicaSinIdentificarAlLlegarAlLimiteLegal()
    {
        const long importe=ReglasConsumidorFinalV17.LimiteIdentificacionObligatoriaCentavos;
        Assert.NotNull(ReglasConsumidorFinalV17.Resolver(6,importe-1,Catalogo));
        Assert.Null(ReglasConsumidorFinalV17.Resolver(6,importe,Catalogo));
        var hoy=DateOnly.FromDateTime(DateTime.Today);
        var factura=new ComprobanteFiscalWsfe(
            3,6,1,hoy,2,99,0,5,10_000_000m,
            0,10_000_000m,0,0,0,
            Array.Empty<IvaFiscalWsfe>(),hoy,hoy,hoy);
        Assert.Throws<InvalidOperationException>(()=>
            ReglasConsumidorFinalV17.ValidarIdentificacion(factura));
    }

    [Fact]
    public void GuardaLaPreferenciaPorCuitYEsReversible()
    {
        var dir=Path.Combine(Path.GetTempPath(),"AparcarCF17",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var db=new AparcarDb(Path.Combine(dir,"aparcar.db"));
            db.Inicializar();
            db.CrearAdministradorInicial("AdminCF","ClaveSegura2026");
            db.IniciarSesion("AdminCF","ClaveSegura2026");
            Assert.False(db.ConsumidorFinalPredeterminado("20123456786"));
            db.ConfigurarConsumidorFinalPredeterminado("20123456786",true);
            Assert.True(db.ConsumidorFinalPredeterminado("20123456786"));
            Assert.False(db.ConsumidorFinalPredeterminado("30000000007"));
            db.ConfigurarConsumidorFinalPredeterminado("20123456786",false);
            Assert.False(db.ConsumidorFinalPredeterminado("20123456786"));
        }
        finally {SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }

    [Fact]
    public void DebeIdentificarseCuandoElClienteLoSolicitaAunBajoMonto()
    {
        Assert.Null(ReglasConsumidorFinalV17.Resolver(6,10000,Catalogo,
            requiereIdentificacion:true));
        Assert.NotNull(ReglasConsumidorFinalV17.Resolver(6,10000,Catalogo,
            requiereIdentificacion:false));
    }

    [Fact]
    public void NoAdivinaLaCondicionIvaConDuplicados()
    {
        var lista=new[]{
            new CondicionIvaReceptorWsfe(5,"Consumidor Final","B"),
            new CondicionIvaReceptorWsfe(7,"Consumidor Final","B")
        };
        Assert.Null(ReglasConsumidorFinalV17.Resolver(6,10000,lista));
    }
}
