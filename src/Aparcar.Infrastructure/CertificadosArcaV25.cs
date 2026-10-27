using System;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aparcar.Infrastructure;

public sealed record SolicitudCertificadoArca(string CsrPem,string ClavePrivadaProtegidaPem);

/// <summary>Genera una solicitud local. Solo ARCA puede emitir el certificado.</summary>
public static class CertificadosArcaV25
{
    private static void ValidarClave(string clave)
    {
        if(string.IsNullOrWhiteSpace(clave) || clave.Length is <12 or >256)
            throw new ArgumentException("Usá una contraseña de 12 a 256 caracteres.");
    }

    private static void ValidarNombre(string valor,string campo,int max)
    {
        if(string.IsNullOrWhiteSpace(valor) || valor.Trim().Length>max)
            throw new ArgumentException($"Revisá {campo}.");
        foreach(var c in valor)
            if(char.IsControl(c))throw new ArgumentException($"Revisá {campo}.");
    }

    public static SolicitudCertificadoArca CrearSolicitud(string cuit,string organizacion,
        string sistema,string clave)
    {
        cuit=(cuit??"").Trim();
        if(!AdaptadorWsfeFacturador.CuitValido(cuit))
            throw new ArgumentException("Ingresá un CUIT válido de 11 dígitos.");
        ValidarNombre(organizacion,"la razón social",80);
        ValidarNombre(sistema,"el nombre del sistema",64);
        ValidarClave(clave);
        using var rsa=RSA.Create(2048);
        // RDN conforme al formato publicado por ARCA; ASN.1 evita inyección en el DN.
        var asn=new AsnWriter(AsnEncodingRules.DER);
        asn.PushSequence();
        void Rdn(string oid,UniversalTagNumber tag,string valor)
        {
            asn.PushSetOf();asn.PushSequence();asn.WriteObjectIdentifier(oid);
            asn.WriteCharacterString(tag,valor);asn.PopSequence();asn.PopSetOf();
        }
        Rdn("2.5.4.6",UniversalTagNumber.PrintableString,"AR");
        Rdn("2.5.4.10",UniversalTagNumber.UTF8String,organizacion.Trim());
        Rdn("2.5.4.3",UniversalTagNumber.UTF8String,sistema.Trim());
        Rdn("2.5.4.5",UniversalTagNumber.PrintableString,"CUIT "+cuit);
        asn.PopSequence();
        var req=new CertificateRequest(new X500DistinguishedName(asn.Encode()),rsa,
            HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var csr=req.CreateSigningRequestPem();
        var pbe=new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc,HashAlgorithmName.SHA256,210000);
        return new SolicitudCertificadoArca(csr,rsa.ExportEncryptedPkcs8PrivateKeyPem(clave,pbe));
    }

    public static byte[] CrearPfx(byte[] certificado,string clavePrivadaPem,
        string clavePrivada,string clavePfx)
    {
        ArgumentNullException.ThrowIfNull(certificado);
        ValidarClave(clavePfx);
        using var rsa=RSA.Create();
        try {rsa.ImportFromEncryptedPem(clavePrivadaPem,clavePrivada);}
        catch(CryptographicException ex)
        {throw new ArgumentException("No se pudo abrir la clave protegida. Revisá la contraseña y el archivo.",ex);}
        var texto=System.Text.Encoding.UTF8.GetString(certificado);
        using var cert=texto.Contains("-----BEGIN CERTIFICATE-----",StringComparison.Ordinal)
            ?X509Certificate2.CreateFromPem(texto)
            :X509CertificateLoader.LoadCertificate(certificado);
        if(cert.NotAfter.ToUniversalTime()<=DateTime.UtcNow || cert.NotBefore.ToUniversalTime()>DateTime.UtcNow)
            throw new ArgumentException("El certificado está vencido o todavía no es válido.");
        try
        {
            using var conClave=cert.CopyWithPrivateKey(rsa);
            return conClave.Export(X509ContentType.Pfx,clavePfx);
        }
        catch(ArgumentException ex)
        {throw new ArgumentException("El certificado y la clave privada no corresponden. Elegí la clave usada para el CSR.",ex);}
    }
}
