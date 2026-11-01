using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

public sealed class EmailConfigurationTests
{
    [Fact]
    public void Production_delivery_refuses_to_start_without_persistent_protected_keys()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["EmailDelivery:Enabled"] = "true" }).Build();
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => services.AddAccountSecurity(configuration, new Environment()));
        Assert.Contains("persistent Data Protection", error.Message);
    }

    [Fact]
    public void Configured_keys_are_persisted_with_certificate_encryption_and_work_across_instances()
    {
        var directory = Path.Combine(Path.GetTempPath(), "myshoppinglist-key-fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=local-key-fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            var path = Path.Combine(directory, "fixture.pfx"); File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, "fixture-password"));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["AccountSecurity:KeyDirectory"] = Path.Combine(directory, "keys"), ["AccountSecurity:KeyCertificatePath"] = path,
                ["AccountSecurity:KeyCertificatePassword"] = "fixture-password"
            }).Build();
            ServiceProvider Provider()
            {
                var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(TimeProvider.System);
                services.AddAccountSecurity(configuration, new Environment()); return services.BuildServiceProvider();
            }
            string protectedValue;
            using (var first = Provider()) protectedValue = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture").Protect("fixture-secret");
            using (var second = Provider()) Assert.Equal("fixture-secret", second.GetRequiredService<IDataProtectionProvider>().CreateProtector("fixture").Unprotect(protectedValue));
            var xml = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(directory, "keys"), "key-*.xml")));
            Assert.Contains("encryptedSecret", xml); Assert.DoesNotContain("<masterKey", xml); Assert.DoesNotContain("fixture-secret", xml);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Fixture";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
