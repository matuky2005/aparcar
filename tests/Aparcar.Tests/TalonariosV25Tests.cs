using System;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class TalonariosV25Tests
{
    [Fact]
    public void MaestroSeparaAmbientesSinCrearNumerosYElTalonarioXConservaSuSecuencia()
    {
        var dir=Path.Combine(Path.GetTempPath(),"AparcarTalonarios25",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var db=new AparcarDb(Path.Combine(dir,"aparcar.db"));
            db.Inicializar();
            db.CrearAdministradorInicial("AdminV25","ClaveSegura2026");
            db.IniciarSesion("AdminV25","ClaveSegura2026");
            db.AbrirTurno("Cajero",0);
            db.CrearTalonarioX("0001","Central");
            db.ConfigurarTalonarioXParaCobros("0001");
            var servicio=db.GuardarServicioLavado("Lavado completo","Auto",12100);
            var orden=db.CrearOrdenLavado(servicio.Id,"AA123BC","Operario","");
            var cobro=db.CobrarComplemento("Lavadero",orden.Id,MediosPago.Efectivo,12100,"",false,Guid.NewGuid());
            Assert.Equal(1,db.EmitirTicketX("0001","Lavadero",cobro.Id).Ticket.Numero);
            var suspendido=db.ActualizarTalonarioX("0001","Central renovado",false);
            Assert.Equal(1,suspendido.UltimoNumero);
            Assert.False(db.ObtenerTalonarioXParaCobros()!.Habilitado);
            var orden2=db.CrearOrdenLavado(servicio.Id,"AB123CD","Operario","");
            var cobro2=db.CobrarComplemento("Lavadero",orden2.Id,MediosPago.Efectivo,12100,"",false,Guid.NewGuid());
            Assert.Throws<InvalidOperationException>(()=>db.EmitirTicketX("0001","Lavadero",cobro2.Id));
            Assert.Equal(1,db.ListarTalonariosX().Single().UltimoNumero);
            db.ActualizarTalonarioX("0001","Central renovado",true);
            Assert.Equal(2,db.EmitirTicketX("0001","Lavadero",cobro2.Id).Ticket.Numero);
            var prueba=new TalonarioFiscal("Homologacion","20123456786",1,6,"Prueba B",true);
            var produccion=prueba with {Ambiente="Produccion",Nombre="Producción B"};
            db.GuardarTalonarioFiscal(prueba);
            db.GuardarTalonarioFiscal(produccion);
            db.GuardarTalonarioFiscal(prueba with {Habilitado=false});
            Assert.Equal(2,db.ListarTalonariosFiscales().Count);
            Assert.False(db.ListarTalonariosFiscales().Single(x=>x.Ambiente=="Homologacion").Habilitado);
            Assert.Throws<ArgumentException>(()=>db.GuardarTalonarioFiscal(prueba with {Cuit="20123456789"}));
            db.CrearUsuario("cajero25","ClaveCajero2026",RolesAparcar.Cajero);
            db.CerrarSesion();
            db.IniciarSesion("cajero25","ClaveCajero2026");
            Assert.Throws<UnauthorizedAccessException>(()=>db.GuardarTalonarioFiscal(prueba));
            Assert.Throws<UnauthorizedAccessException>(()=>db.ActualizarTalonarioX("0001","Cambio ilegal",false));
            db.CerrarSesion();
            var otraInstancia=new AparcarDb(db.RutaDb);
            otraInstancia.Inicializar();
            otraInstancia.IniciarSesion("AdminV25","ClaveSegura2026");
            Assert.Equal(2,otraInstancia.ListarTalonariosFiscales().Count);
            Assert.Equal(2,otraInstancia.ListarTalonariosX().Single().UltimoNumero);
        }
        finally {SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }
}
