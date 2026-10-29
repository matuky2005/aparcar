namespace Aparcar.Domain;

public static class ModosSector
{
    public const string Libre = "Libre";
    public const string Asignado = "Asignado";
    public static bool EsValido(string modo) => modo is Libre or Asignado;
}

public sealed record SectorEstacionamiento(
    string Nombre,int Capacidad,string Modo,bool Activo,int Ocupados,int PlazasDisponibles)
{
    public int Libres => Math.Max(0,Math.Min(Capacidad-Ocupados,
        Modo==ModosSector.Asignado?PlazasDisponibles:Capacidad-Ocupados));
}

public sealed record PlazaEstacionamiento(
    string Sector,string Codigo,bool Habilitada,bool Ocupada,string? Patente);

public sealed record UbicacionEstadia(
    Guid EstadiaId,string Sector,string? Plaza,string Patente);
