using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.X509;

namespace PdfEditor.Core;

/// <summary>Generates self-signed signing certificates for users who do not have one.</summary>
public static class CertificateFactory
{
    /// <summary>Creates a self-signed RSA-2048 certificate and returns it as PKCS#12 bytes.</summary>
    public static byte[] CreateSelfSignedPkcs12(string commonName, string password, int validYears = 5)
    {
        var random = new SecureRandom();
        var keyGen = new RsaKeyPairGenerator();
        keyGen.Init(new KeyGenerationParameters(random, 2048));
        AsymmetricCipherKeyPair pair = keyGen.GenerateKeyPair();

        var dn = new X509Name($"CN={commonName}");
        var generator = new X509V3CertificateGenerator();
        generator.SetSerialNumber(BigIntegers.CreateRandomInRange(
            BigInteger.One, BigInteger.ValueOf(long.MaxValue), random));
        generator.SetIssuerDN(dn);
        generator.SetSubjectDN(dn);
        generator.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        generator.SetNotAfter(DateTime.UtcNow.AddYears(validYears));
        generator.SetPublicKey(pair.Public);
        generator.AddExtension(X509Extensions.KeyUsage, true,
            new KeyUsage(KeyUsage.DigitalSignature | KeyUsage.NonRepudiation));

        var certificate = generator.Generate(
            new Asn1SignatureFactory("SHA256WITHRSA", pair.Private, random));

        var store = new Pkcs12StoreBuilder().Build();
        store.SetKeyEntry("signing-key", new AsymmetricKeyEntry(pair.Private),
            new[] { new X509CertificateEntry(certificate) });
        using var output = new MemoryStream();
        store.Save(output, password.ToCharArray(), random);
        return output.ToArray();
    }
}
