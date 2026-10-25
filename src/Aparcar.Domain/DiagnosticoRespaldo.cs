namespace Aparcar.Domain;

public sealed record DiagnosticoRespaldo(
    string Ruta,long TamanoBytes,string HashSha256,
    int VersionEsquema,long CantidadEstadias,bool IntegridadCorrecta);
