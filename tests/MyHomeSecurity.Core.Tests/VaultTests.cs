using MyHomeSecurity.Core.Models;
using MyHomeSecurity.Core.Security;

namespace MyHomeSecurity.Core.Tests;

public sealed class VaultTests : IDisposable
{
    // The lowest count Unlock accepts: the tests are about the format, not the KDF cost.
    private const int Iterations = 100_000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mhs-vault-" + Guid.NewGuid().ToString("N"));

    private string VaultPath => Path.Combine(_dir, "vault.json");

    public VaultTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void RoundTripsDataAndKeepsSecretsOutOfTheFile()
    {
        var vault = new Vault(VaultPath);
        var data = vault.Create("correct horse", Iterations);
        data.Cameras.Add(new CameraProfile { Name = "Front door", Host = "192.168.1.50", Username = "admin", Password = "camera-secret-123" });
        data.HomeAssistant.ServerUrl = "http://homeassistant.local:8123";
        data.HomeAssistant.Token = "ha-token-abcdef";
        data.HomeAssistant.Boosts["climate.heating"] = new HaBoostRecord(22.5, 60, new DateTimeOffset(2026, 1, 2, 19, 0, 0, TimeSpan.Zero));
        vault.Save(data);

        var raw = File.ReadAllText(VaultPath);
        Assert.DoesNotContain("camera-secret-123", raw);
        Assert.DoesNotContain("ha-token-abcdef", raw);
        Assert.DoesNotContain("Front door", raw);

        var reopened = new Vault(VaultPath).Unlock("correct horse");
        var camera = Assert.Single(reopened.Cameras);
        Assert.Equal("Front door", camera.Name);
        Assert.Equal("camera-secret-123", camera.Password);
        Assert.Equal("ha-token-abcdef", reopened.HomeAssistant.Token);
        Assert.Equal(new HaBoostRecord(22.5, 60, new DateTimeOffset(2026, 1, 2, 19, 0, 0, TimeSpan.Zero)),
            reopened.HomeAssistant.Boosts["climate.heating"]);
    }

    [Fact]
    public void WrongPasswordFails()
    {
        new Vault(VaultPath).Create("correct horse", Iterations);
        var vault = new Vault(VaultPath);
        Assert.Throws<VaultException>(() => vault.Unlock("wrong password"));
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public void CreateRefusesToOverwrite()
    {
        new Vault(VaultPath).Create("correct horse", Iterations);
        Assert.Throws<VaultException>(() => new Vault(VaultPath).Create("another password", Iterations));
    }

    [Fact]
    public void ShortPasswordsAreRefused()
    {
        Assert.Throws<VaultException>(() => new Vault(VaultPath).Create("short", Iterations));
        Assert.False(File.Exists(VaultPath));
    }

    [Fact]
    public void TamperedFileFails()
    {
        new Vault(VaultPath).Create("correct horse", Iterations);
        var text = File.ReadAllText(VaultPath);
        Assert.Contains($"\"Iterations\":{Iterations}", text);
        // Changing the KDF parameters must fail authentication: they're bound in as associated data.
        File.WriteAllText(VaultPath, text.Replace($"\"Iterations\":{Iterations}", $"\"Iterations\":{Iterations + 1}"));
        Assert.Throws<VaultException>(() => new Vault(VaultPath).Unlock("correct horse"));
    }

    [Fact]
    public void ChangePasswordNeedsTheCurrentOne()
    {
        var vault = new Vault(VaultPath);
        var data = vault.Create("correct horse", Iterations);
        data.HomeAssistant.Token = "kept";

        Assert.Throws<VaultException>(() => vault.ChangePassword("not it at all", "battery staple", data));
        vault.ChangePassword("correct horse", "battery staple", data);

        Assert.Throws<VaultException>(() => new Vault(VaultPath).Unlock("correct horse"));
        Assert.Equal("kept", new Vault(VaultPath).Unlock("battery staple").HomeAssistant.Token);
    }

    [Fact]
    public void SubkeysDifferByPurposeAndPasswordAndNeedAnUnlockedVault()
    {
        var vault = new Vault(VaultPath);
        var data = vault.Create("correct horse", Iterations);
        var pictures = vault.DeriveSubkey("last-pictures");
        Assert.NotNull(pictures);
        Assert.Equal(32, pictures.Length);
        Assert.Equal(pictures, vault.DeriveSubkey("last-pictures"));
        Assert.NotEqual(pictures, vault.DeriveSubkey("something-else"));

        vault.ChangePassword("correct horse", "battery staple", data);
        Assert.NotEqual(pictures, vault.DeriveSubkey("last-pictures"));

        vault.Lock();
        Assert.Null(vault.DeriveSubkey("last-pictures"));
    }

    [Fact]
    public void LockedVaultCannotSave()
    {
        var vault = new Vault(VaultPath);
        var data = vault.Create("correct horse", Iterations);
        vault.Lock();
        Assert.False(vault.IsUnlocked);
        Assert.Throws<VaultException>(() => vault.Save(data));
    }

    [Fact]
    public void ForgottenVaultIsSetAsideNotDeleted()
    {
        var vault = new Vault(VaultPath);
        vault.Create("correct horse", Iterations);

        var aside = vault.SetAsideForgotten();

        Assert.False(vault.Exists);
        Assert.True(File.Exists(aside));
        Assert.StartsWith("vault.forgotten-", Path.GetFileName(aside));
        // Still the same encrypted vault: rename it back and the old password works.
        Assert.NotNull(new Vault(aside).Unlock("correct horse"));
        // And a new one can be created.
        Assert.NotNull(new Vault(VaultPath).Create("new password", Iterations));
    }
}
