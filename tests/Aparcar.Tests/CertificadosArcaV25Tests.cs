using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Aparcar.Infrastructure;
using Xunit;

namespace Aparcar.Tests;

public sealed class CertificadosArcaV25Tests
{
    private const string Pass="ClavePrueba1234!";
    [Fact]
    public void SolicitudTieneCuitYClaveCifradaYProducePfxConCertificadoCorrespondiente()
    {
        var solicitud=CertificadosArcaV25.CrearSolicitud("20123456786","Empresa prueba","Aparcar",Pass);
        Assert.Contains("BEGIN ENCRYPTED PRIVATE KEY",solicitud.ClavePrivadaProtegidaPem);
        var pedido=CertificateRequest.LoadSigningRequestPem(solicitud.CsrPem,
            HashAlgorithmName.SHA256,CertificateRequestLoadOptions.Default,RSASignaturePadding.Pkcs1);
        Assert.Contains("20123456786",pedido.SubjectName.Name);
        using var clave=RSA.Create();
        clave.ImportFromEncryptedPem(solicitud.ClavePrivadaProtegidaPem,Pass);
        Assert.Equal(clave.ExportSubjectPublicKeyInfo(),pedido.PublicKey.ExportSubjectPublicKeyInfo());
        var emision=new CertificateRequest("CN=Certificado de prueba",clave,
            HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var cert=emision.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddDays(1));
        var pfx=CertificadosArcaV25.CrearPfx(Encoding.UTF8.GetBytes(cert.ExportCertificatePem()),
            solicitud.ClavePrivadaProtegidaPem,Pass,"OtraClaveSegura123!");
        using var resultado=X509CertificateLoader.LoadPkcs12(pfx,"OtraClaveSegura123!",X509KeyStorageFlags.EphemeralKeySet);
        Assert.True(resultado.HasPrivateKey);
    }

    [Fact]
    public void NoAceptaCertificadoDeOtraClaveNiContrasenaIncorrecta()
    {
        var a=CertificadosArcaV25.CrearSolicitud("20123456786","Empresa prueba","Aparcar",Pass);
        using var otra=RSA.Create(2048);
        var req=new CertificateRequest("CN=Otra clave",otra,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var cert=req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddDays(1));
        var bytes=Encoding.UTF8.GetBytes(cert.ExportCertificatePem());
        Assert.Throws<ArgumentException>(()=>CertificadosArcaV25.CrearPfx(bytes,a.ClavePrivadaProtegidaPem,Pass,Pass));
        Assert.Throws<ArgumentException>(()=>CertificadosArcaV25.CrearPfx(bytes,a.ClavePrivadaProtegidaPem,"Incorrecta",Pass));
        Assert.Throws<ArgumentException>(()=>CertificadosArcaV25.CrearSolicitud("20123456789","Empresa","App",Pass));
        Assert.Throws<ArgumentException>(()=>CertificadosArcaV25.CrearSolicitud("20123456786","Empresa","App","corta"));
    }
}
