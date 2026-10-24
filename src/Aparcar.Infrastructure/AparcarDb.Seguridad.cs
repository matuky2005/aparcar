using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

/// <summary>
/// Authentication and tamper-evident, append-only audit for the local single-PC pilot.
/// Physical access to the SQLite file is outside the threat boundary.
/// </summary>
public sealed partial class AparcarDb
{
    private UsuarioSesion? _sesionActual;
    private DateTimeOffset _ultimaActividadUtc;
    private static readonly TimeSpan InactividadMaxima=TimeSpan.FromMinutes(20);
    private const int IteracionesContrasena=210_000;

    private void InicializarSeguridad()
    {
        ResguardarMigracionSeguridad();
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS Usuarios(
                Id TEXT PRIMARY KEY,
                Nombre TEXT NOT NULL,
                Normalizado TEXT NOT NULL UNIQUE,
                Rol TEXT NOT NULL CHECK(Rol IN
                    ('Administrador','Supervisor','Cajero','Lavadero','Consulta')),
                Salt BLOB NOT NULL,
                Hash BLOB NOT NULL,
                Iteraciones INTEGER NOT NULL,
                Activo INTEGER NOT NULL DEFAULT 1 CHECK(Activo IN (0,1)),
                Fallos INTEGER NOT NULL DEFAULT 0,
                BloqueoHastaUtc TEXT,
                CreadoUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS AuditoriaSeguridad(
                Secuencia INTEGER PRIMARY KEY AUTOINCREMENT,
                Id TEXT NOT NULL UNIQUE,
                FechaUtc TEXT NOT NULL,
                Actor TEXT NOT NULL,
                Accion TEXT NOT NULL,
                Objeto TEXT NOT NULL,
                Detalle TEXT NOT NULL,
                HashPrevio TEXT NOT NULL,
                HashActual TEXT NOT NULL
            );
            INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                VALUES(5,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private void ResguardarMigracionSeguridad()
    {
        // Se usa la API de backup de SQLite; jamás se reemplaza la base activa.
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT COUNT(*) FROM EsquemaVersion WHERE Numero=5";
        if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)==0)
            CrearRespaldo();
    }

    public bool HayAdministrador()
    {
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT COUNT(*) FROM Usuarios WHERE Rol='Administrador' AND Activo=1";
        return Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)>0;
    }

    private static string NormalizarUsuario(string nombre) =>
        (nombre ?? "").Trim().ToUpperInvariant();

    private static void ValidarCredenciales(string nombre,string contrasena)
    {
        if(string.IsNullOrWhiteSpace(nombre)||nombre.Trim().Length is < 3 or > 60)
            throw new ArgumentException("El usuario debe tener entre 3 y 60 caracteres.");
        if(string.IsNullOrWhiteSpace(contrasena)||contrasena.Length<10 ||
           contrasena.Length>128 ||
           !contrasena.Any(char.IsLetter) || !contrasena.Any(char.IsDigit))
            throw new ArgumentException("La contraseña debe tener 10 a 128 caracteres, con letras y números.");
    }

    public UsuarioListado CrearAdministradorInicial(string nombre,string contrasena)
    {
        ValidarCredenciales(nombre,contrasena);
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="SELECT COUNT(*) FROM Usuarios";
            if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)!=0)
                throw new InvalidOperationException("Ya existen usuarios. Usá la gestión de administradores.");
        }
        var creado=InsertarUsuario(db,tx,nombre,contrasena,RolesAparcar.Administrador);
        Auditar(db,tx,nombre.Trim(),"Usuario.Inicial",creado.Id.ToString("D"),
            "Administrador inicial creado");
        tx.Commit();
        return creado;
    }

    public UsuarioListado CrearUsuario(string nombre,string contrasena,string rol)
    {
        ExigirPermiso(PermisosAparcar.Usuarios);
        if(!RolesAparcar.Valido(rol))throw new ArgumentException("Rol desconocido.");
        ValidarCredenciales(nombre,contrasena);
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        var creado=InsertarUsuario(db,tx,nombre,contrasena,rol);
        AuditarSesion(db,tx,"Usuario.Crear",creado.Id.ToString("D"),$"Rol: {rol}");
        tx.Commit();
        return creado;
    }

    private static UsuarioListado InsertarUsuario(SqliteConnection db,SqliteTransaction tx,
        string nombre,string contrasena,string rol)
    {
        var salt=RandomNumberGenerator.GetBytes(24);
        var hash=Rfc2898DeriveBytes.Pbkdf2(contrasena,salt,IteracionesContrasena,
            HashAlgorithmName.SHA256,32);
        var id=Guid.NewGuid();
        var ahora=DateTimeOffset.UtcNow;
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO Usuarios(Id,Nombre,Normalizado,Rol,Salt,Hash,Iteraciones,CreadoUtc)
            VALUES($id,$nombre,$normalizado,$rol,$salt,$hash,$iteraciones,$fecha);
            """;
        cmd.Parameters.AddWithValue("$id",id.ToString("D"));
        cmd.Parameters.AddWithValue("$nombre",nombre.Trim());
        cmd.Parameters.AddWithValue("$normalizado",NormalizarUsuario(nombre));
        cmd.Parameters.AddWithValue("$rol",rol);
        cmd.Parameters.AddWithValue("$salt",salt);
        cmd.Parameters.AddWithValue("$hash",hash);
        cmd.Parameters.AddWithValue("$iteraciones",IteracionesContrasena);
        cmd.Parameters.AddWithValue("$fecha",ahora.ToString("O"));
        try { cmd.ExecuteNonQuery(); }
        catch(SqliteException ex) when(ex.SqliteErrorCode==19)
        { throw new InvalidOperationException("Ya existe ese usuario.",ex); }
        return new UsuarioListado(id,nombre.Trim(),rol,true,ahora);
    }

    public UsuarioSesion IniciarSesion(string nombre,string contrasena)
    {
        var normalized=NormalizarUsuario(nombre);
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        Guid id;
        string real,rol,lockout;
        byte[] salt,hash;
        int iter,fallos;
        bool activo;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT Id,Nombre,Rol,Salt,Hash,Iteraciones,Activo,Fallos,
                       COALESCE(BloqueoHastaUtc,'')
                FROM Usuarios WHERE Normalizado=$nombre;
                """;
            cmd.Parameters.AddWithValue("$nombre",normalized);
            using var r=cmd.ExecuteReader();
            if(!r.Read())throw new InvalidOperationException("Credenciales incorrectas.");
            id=Guid.Parse(r.GetString(0));
            real=r.GetString(1);
            rol=r.GetString(2);
            salt=(byte[])r.GetValue(3);
            hash=(byte[])r.GetValue(4);
            iter=r.GetInt32(5);
            activo=r.GetInt32(6)==1;
            fallos=r.GetInt32(7);
            lockout=r.GetString(8);
        }
        if(!activo)throw new InvalidOperationException("Cuenta deshabilitada.");
        var ahora=DateTimeOffset.UtcNow;
        if(lockout.Length>0 && DateTimeOffset.Parse(lockout,CultureInfo.InvariantCulture,
           DateTimeStyles.RoundtripKind)>ahora)
            throw new InvalidOperationException("Cuenta bloqueada temporalmente.");

        var calculado=Rfc2898DeriveBytes.Pbkdf2(contrasena??"",salt,iter,
            HashAlgorithmName.SHA256,hash.Length);
        var valido=CryptographicOperations.FixedTimeEquals(calculado,hash);
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText=valido
                ? "UPDATE Usuarios SET Fallos=0,BloqueoHastaUtc=NULL WHERE Id=$id"
                : """
                    UPDATE Usuarios SET Fallos=$fallos,BloqueoHastaUtc=$bloqueo WHERE Id=$id
                    """;
            cmd.Parameters.AddWithValue("$id",id.ToString("D"));
            if(!valido)
            {
                cmd.Parameters.AddWithValue("$fallos",fallos+1>=5?0:fallos+1);
                cmd.Parameters.AddWithValue("$bloqueo",fallos+1>=5
                    ? ahora.AddMinutes(10).ToString("O"):(object)DBNull.Value);
            }
            cmd.ExecuteNonQuery();
        }
        Auditar(db,tx,real,valido?"Sesion.Ingresar":"Sesion.Fallida",
            id.ToString("D"),valido?"Acceso concedido":"Contraseña incorrecta");
        tx.Commit();
        if(!valido)throw new InvalidOperationException("Credenciales incorrectas.");
        _sesionActual=new UsuarioSesion(id,real,rol,ahora);
        _ultimaActividadUtc=ahora;
        return _sesionActual;
    }

    public UsuarioSesion? SesionActual
    {
        get
        {
            if(_sesionActual is not null &&
                DateTimeOffset.UtcNow-_ultimaActividadUtc>InactividadMaxima)
                _sesionActual=null;
            return _sesionActual;
        }
    }

    public void CerrarSesion()
    {
        if(_sesionActual is not null)
        {
            using var db=Abrir();
            using var tx=db.BeginTransaction();
            AuditarSesion(db,tx,"Sesion.Salir",_sesionActual.Id.ToString("D"),
                "Sesión cerrada");
            tx.Commit();
        }
        _sesionActual=null;
    }

    public void ExigirPermiso(string permiso)
    {
        // Fallar cerrado: ningún método operativo funciona sin el primer
        // administrador. La única excepción es CrearAdministradorInicial.
        if(!HayAdministrador())
            throw new UnauthorizedAccessException("Configurá el administrador inicial para operar.");
        var sesion=SesionActual;
        if(sesion is null)
            throw new UnauthorizedAccessException("Iniciá sesión para continuar.");
        using(var db=Abrir())
        using(var cmd=db.CreateCommand())
        {
            cmd.CommandText="SELECT Rol,Activo FROM Usuarios WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id",sesion.Id.ToString("D"));
            using var r=cmd.ExecuteReader();
            if(!r.Read() || r.GetInt32(1)!=1 || r.GetString(0)!=sesion.Rol)
            {
                _sesionActual=null;
                throw new UnauthorizedAccessException("Tu cuenta o rol cambió. Volvé a iniciar sesión.");
            }
        }
        // El rol y la vigencia se consultan en SQLite en cada operación. Si otro
        // administrador revoca la cuenta o modifica el rol, la sesión pierde acceso.
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT Rol,Activo FROM Usuarios WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id",sesion.Id.ToString("D"));
        using var r=cmd.ExecuteReader();
        if(!r.Read() || r.GetInt32(1)!=1)
        {
            _sesionActual=null;
            throw new UnauthorizedAccessException("El usuario ya no está habilitado.");
        }
        var rolActual=r.GetString(0);
        if(!RolesAparcar.Permite(rolActual,permiso))
            throw new UnauthorizedAccessException($"Tu rol ({rolActual}) no permite esta operación.");
        _ultimaActividadUtc=DateTimeOffset.UtcNow;
    }

    private static (Guid Id,string Rol,bool Activo) LeerUsuarioDestino(
        SqliteConnection db,SqliteTransaction tx,Guid usuarioId)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="SELECT Id,Rol,Activo FROM Usuarios WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id",usuarioId.ToString("D"));
        using var r=cmd.ExecuteReader();
        if(!r.Read())throw new ArgumentException("Usuario inexistente.");
        return(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetInt32(2)==1);
    }

    private static long ContarAdministradores(SqliteConnection db,SqliteTransaction tx)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="SELECT COUNT(*) FROM Usuarios WHERE Rol='Administrador' AND Activo=1";
        return Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);
    }

    public void CambiarRolUsuario(Guid usuarioId,string nuevoRol)
    {
        ExigirPermiso(PermisosAparcar.Usuarios);
        if(!RolesAparcar.Valido(nuevoRol))throw new ArgumentException("Rol inválido.");
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        var usuario=LeerUsuarioDestino(db,tx,usuarioId);
        if(usuario.Id==_sesionActual?.Id && nuevoRol!=RolesAparcar.Administrador)
            throw new InvalidOperationException("No podés quitarte el rol administrador durante tu sesión.");
        if(usuario.Rol==RolesAparcar.Administrador && nuevoRol!=RolesAparcar.Administrador &&
            ContarAdministradores(db,tx)<=1)
            throw new InvalidOperationException("Debe conservarse un administrador activo.");
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="UPDATE Usuarios SET Rol=$rol WHERE Id=$id";
        cmd.Parameters.AddWithValue("$rol",nuevoRol);
        cmd.Parameters.AddWithValue("$id",usuarioId.ToString("D"));
        cmd.ExecuteNonQuery();
        AuditarSesion(db,tx,"Usuario.Rol",usuarioId.ToString("D"),
            $"Antes={usuario.Rol}; ahora={nuevoRol}");
        tx.Commit();
    }

    public void DesactivarUsuario(Guid usuarioId)
    {
        ExigirPermiso(PermisosAparcar.Usuarios);
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        var usuario=LeerUsuarioDestino(db,tx,usuarioId);
        if(usuario.Id==_sesionActual?.Id)
            throw new InvalidOperationException("No podés desactivar tu propia cuenta.");
        if(usuario.Rol==RolesAparcar.Administrador && usuario.Activo &&
            ContarAdministradores(db,tx)<=1)
            throw new InvalidOperationException("No se puede desactivar al último administrador.");
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="UPDATE Usuarios SET Activo=0 WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id",usuarioId.ToString("D"));
        cmd.ExecuteNonQuery();
        AuditarSesion(db,tx,"Usuario.Desactivar",usuarioId.ToString("D"),"Cuenta inhabilitada");
        tx.Commit();
    }

    public void RestablecerContrasenaUsuario(Guid usuarioId,string nueva)
    {
        ExigirPermiso(PermisosAparcar.Usuarios);
        ValidarCredenciales("usuario",nueva);
        var salt=RandomNumberGenerator.GetBytes(24);
        var hash=Rfc2898DeriveBytes.Pbkdf2(nueva,salt,IteracionesContrasena,HashAlgorithmName.SHA256,32);
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        _=LeerUsuarioDestino(db,tx,usuarioId);
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            UPDATE Usuarios SET Salt=$salt,Hash=$hash,Iteraciones=$iter,
                Fallos=0,BloqueoHastaUtc=NULL WHERE Id=$id;
            """;
        cmd.Parameters.AddWithValue("$salt",salt);
        cmd.Parameters.AddWithValue("$hash",hash);
        cmd.Parameters.AddWithValue("$iter",IteracionesContrasena);
        cmd.Parameters.AddWithValue("$id",usuarioId.ToString("D"));
        cmd.ExecuteNonQuery();
        AuditarSesion(db,tx,"Usuario.Clave",usuarioId.ToString("D"),"Contraseña restablecida por administrador");
        tx.Commit();
    }

    /// <summary>
    /// Modifica rol/estado con autorización de administrador. Jamás permite
    /// revocar el último administrador activo o deshabilitar la propia cuenta.
    /// </summary>
    public UsuarioListado ModificarUsuario(Guid id,string rol,bool activo)
    {
        ExigirPermiso(PermisosAparcar.Usuarios);
        if(!RolesAparcar.Valido(rol))throw new ArgumentException("Rol desconocido.");
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        string nombre,rolAnterior,fecha;
        bool activoAnterior;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="SELECT Nombre,Rol,Activo,CreadoUtc FROM Usuarios WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id",id.ToString("D"));
            using var reader=cmd.ExecuteReader();
            if(!reader.Read())throw new ArgumentException("El usuario indicado no existe.");
            nombre=reader.GetString(0);
            rolAnterior=reader.GetString(1);
            activoAnterior=reader.GetInt32(2)==1;
            fecha=reader.GetString(3);
        }
        if(id==_sesionActual?.Id && (!activo || rol!=RolesAparcar.Administrador))
            throw new InvalidOperationException("No podés desactivar ni reducir el rol de tu propia cuenta.");
        if(rolAnterior==RolesAparcar.Administrador && activoAnterior &&
            (!activo || rol!=RolesAparcar.Administrador))
        {
            using var check=db.CreateCommand();
            check.Transaction=tx;
            check.CommandText="SELECT COUNT(*) FROM Usuarios WHERE Activo=1 AND Rol='Administrador'";
            if(Convert.ToInt64(check.ExecuteScalar(),CultureInfo.InvariantCulture)<=1)
                throw new InvalidOperationException("No se puede revocar el último administrador activo.");
        }
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="UPDATE Usuarios SET Rol=$rol,Activo=$activo WHERE Id=$id";
            cmd.Parameters.AddWithValue("$rol",rol);
            cmd.Parameters.AddWithValue("$activo",activo?1:0);
            cmd.Parameters.AddWithValue("$id",id.ToString("D"));
            if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("No se actualizó el usuario.");
        }
        AuditarSesion(db,tx,"Usuario.Modificar",id.ToString("D"),
            $"Rol {rolAnterior}->{rol}; activo {activoAnterior}->{activo}");
        tx.Commit();
        return new UsuarioListado(id,nombre,rol,activo,
            DateTimeOffset.Parse(fecha,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind));
    }

    /// <summary>Permite al usuario autenticado renovar su propia contraseña.</summary>
    public void CambiarMiContrasena(string anterior,string nueva)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        var sesion=_sesionActual!;
        ValidarCredenciales(sesion.Usuario,nueva);
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        byte[] salt,hash;
        int iter;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="SELECT Salt,Hash,Iteraciones FROM Usuarios WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id",sesion.Id.ToString("D"));
            using var reader=cmd.ExecuteReader();
            if(!reader.Read())throw new UnauthorizedAccessException("Usuario no disponible.");
            salt=(byte[])reader.GetValue(0);
            hash=(byte[])reader.GetValue(1);
            iter=reader.GetInt32(2);
        }
        var prueba=Rfc2898DeriveBytes.Pbkdf2(anterior??"",salt,iter,
            HashAlgorithmName.SHA256,hash.Length);
        if(!CryptographicOperations.FixedTimeEquals(prueba,hash))
            throw new UnauthorizedAccessException("Contraseña actual incorrecta.");
        var nuevoSalt=RandomNumberGenerator.GetBytes(24);
        var nuevoHash=Rfc2898DeriveBytes.Pbkdf2(nueva,nuevoSalt,IteracionesContrasena,
            HashAlgorithmName.SHA256,32);
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                UPDATE Usuarios SET Salt=$salt,Hash=$hash,Iteraciones=$iter,
                    Fallos=0,BloqueoHastaUtc=NULL WHERE Id=$id
                """;
            cmd.Parameters.AddWithValue("$salt",nuevoSalt);
            cmd.Parameters.AddWithValue("$hash",nuevoHash);
            cmd.Parameters.AddWithValue("$iter",IteracionesContrasena);
            cmd.Parameters.AddWithValue("$id",sesion.Id.ToString("D"));
            if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("No se actualizó la contraseña.");
        }
        AuditarSesion(db,tx,"Usuario.Contrasena",sesion.Id.ToString("D"),
            "Contraseña cambiada por el titular");
        tx.Commit();
        _sesionActual=null; // Se obliga a ingresar de nuevo con la nueva clave.
    }

    public List<UsuarioListado> ListarUsuarios()
    {
        ExigirPermiso(PermisosAparcar.Usuarios);
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT Id,Nombre,Rol,Activo,CreadoUtc FROM Usuarios ORDER BY Nombre";
        using var r=cmd.ExecuteReader();
        var lista=new List<UsuarioListado>();
        while(r.Read())lista.Add(new UsuarioListado(
            Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),r.GetInt32(3)==1,
            DateTimeOffset.Parse(r.GetString(4),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind)));
        return lista;
    }

    public List<AuditoriaRegistro> ListarAuditoria(int max=200)
    {
        ExigirPermiso(PermisosAparcar.Auditoria);
        if(max is < 1 or > 1000)throw new ArgumentOutOfRangeException(nameof(max));
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT Secuencia,FechaUtc,Actor,Accion,Objeto,Detalle,HashActual
            FROM AuditoriaSeguridad ORDER BY Secuencia DESC LIMIT $limite;
            """;
        cmd.Parameters.AddWithValue("$limite",max);
        using var r=cmd.ExecuteReader();
        var lista=new List<AuditoriaRegistro>();
        while(r.Read())
            lista.Add(new AuditoriaRegistro(r.GetInt64(0),
                DateTimeOffset.Parse(r.GetString(1),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),
                r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6)));
        return lista;
    }

    private void AuditarSesion(SqliteConnection db,SqliteTransaction tx,
        string accion,string objeto,string detalle)
    {
        Auditar(db,tx,_sesionActual?.Usuario??"Sistema",accion,objeto,detalle);
    }

    private static string CalcularHash(string previo,long secuencia,string id,
        string fecha,string actor,string accion,string objeto,string detalle)
    {
        var texto=string.Join("\u001f",previo,secuencia.ToString(CultureInfo.InvariantCulture),
            id,fecha,actor,accion,objeto,detalle);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto)));
    }

    private static void Auditar(SqliteConnection db,SqliteTransaction tx,
        string actor,string accion,string objeto,string detalle)
    {
        long ultima=0;
        string previo="";
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT Secuencia,HashActual FROM AuditoriaSeguridad
                ORDER BY Secuencia DESC LIMIT 1;
                """;
            using var r=cmd.ExecuteReader();
            if(r.Read()){ultima=r.GetInt64(0);previo=r.GetString(1);}
        }
        var secuencia=ultima+1;
        var id=Guid.NewGuid().ToString("D");
        var fecha=DateTimeOffset.UtcNow.ToString("O");
        var hash=CalcularHash(previo,secuencia,id,fecha,actor,accion,objeto,detalle);
        using var insert=db.CreateCommand();
        insert.Transaction=tx;
        insert.CommandText="""
            INSERT INTO AuditoriaSeguridad(Secuencia,Id,FechaUtc,Actor,Accion,
                Objeto,Detalle,HashPrevio,HashActual)
            VALUES($secuencia,$id,$fecha,$actor,$accion,$objeto,$detalle,$previo,$hash);
            """;
        insert.Parameters.AddWithValue("$secuencia",secuencia);
        insert.Parameters.AddWithValue("$id",id);
        insert.Parameters.AddWithValue("$fecha",fecha);
        insert.Parameters.AddWithValue("$actor",actor);
        insert.Parameters.AddWithValue("$accion",accion);
        insert.Parameters.AddWithValue("$objeto",objeto);
        insert.Parameters.AddWithValue("$detalle",detalle);
        insert.Parameters.AddWithValue("$previo",previo);
        insert.Parameters.AddWithValue("$hash",hash);
        insert.ExecuteNonQuery();
    }

    public bool VerificarCadenaAuditoria()
    {
        ExigirPermiso(PermisosAparcar.Auditoria);
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT Secuencia,Id,FechaUtc,Actor,Accion,Objeto,Detalle,HashPrevio,HashActual
            FROM AuditoriaSeguridad ORDER BY Secuencia;
            """;
        using var r=cmd.ExecuteReader();
        string previo="";
        long secuencia=0;
        while(r.Read())
        {
            secuencia++;
            if(r.GetInt64(0)!=secuencia || r.GetString(7)!=previo)return false;
            var hash=CalcularHash(previo,secuencia,r.GetString(1),
                r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6));
            if(hash!=r.GetString(8))return false;
            previo=hash;
        }
        return true;
    }

    public void RegistrarAuditoria(string accion,string objeto,string detalle)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        if(accion.Length>80||objeto.Length>180||detalle.Length>300)
            throw new ArgumentException("Texto de auditoría demasiado largo.");
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        AuditarSesion(db,tx,accion,objeto,detalle);
        tx.Commit();
    }
}
