using System.Security.Cryptography;
using MyHomeSecurity.Core.Cameras;

namespace MyHomeSecurity.Core.Tests;

public sealed class PictureStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "myhomesecurity-pictures-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _picture = "\x89PNG pretend picture of the front door"u8.ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void APictureIsSavedEncryptedAndReadBack()
    {
        var store = new PictureStore(_dir);
        var camera = Guid.NewGuid();
        store.Save(_key, camera, _picture);

        Assert.Equal(_picture, store.Load(_key, camera));
        var file = Assert.Single(Directory.GetFiles(_dir));
        Assert.DoesNotContain("front door", File.ReadAllText(file));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        // Saving again replaces it.
        store.Save(_key, camera, [1, 2, 3]);
        Assert.Equal([1, 2, 3], store.Load(_key, camera));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void AnUnreadablePictureCountsAsMissing()
    {
        var store = new PictureStore(_dir);
        var camera = Guid.NewGuid();
        Assert.Null(store.Load(_key, camera));

        store.Save(_key, camera, _picture);
        // Another key (a new password) can't read it.
        Assert.Null(store.Load(RandomNumberGenerator.GetBytes(32), camera));

        // Nor can another camera's file stand in for it.
        var other = Guid.NewGuid();
        File.Copy(Path.Combine(_dir, $"{camera:N}.bin"), Path.Combine(_dir, $"{other:N}.bin"));
        Assert.Null(store.Load(_key, other));

        // A damaged file is ignored.
        var path = Path.Combine(_dir, $"{camera:N}.bin");
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes);
        Assert.Null(store.Load(_key, camera));

        store.Delete(camera);
        store.Delete(camera);
        Assert.False(File.Exists(path));
    }
}
