using System.Security.Cryptography;

namespace MyHomeSecurity.Core.Cameras;

/// <summary>
/// Each camera's last picture, kept between runs so the grid isn't blank while the cameras connect.
/// One file per camera, named by its ID. The pictures are encrypted (AES-256-GCM) with a key derived
/// from the vault's (<see cref="Security.Vault.DeriveSubkey"/>), so they can only be read while the
/// app is unlocked. The camera's ID is bound in, so one camera's file can't stand in for another's.
/// A file that can't be read (a new password, a damaged file) is treated as missing.
/// </summary>
public sealed class PictureStore
{
    public const string KeyPurpose = "last-pictures";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Header = "MHSPIC1\n"u8.ToArray();

    public PictureStore(string folder) => Folder = folder;

    public string Folder { get; }

    public void Save(byte[] key, Guid cameraId, byte[] picture)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var ciphertext = new byte[picture.Length];
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, picture, ciphertext, tag, AssociatedData(cameraId));

        CreateFolder();
        var path = PathOf(cameraId);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, PrivateFile()))
            {
                fs.Write(Header);
                fs.Write(nonce);
                fs.Write(tag);
                fs.Write(ciphertext);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>The camera's last picture, or null if there's none or it can't be read.</summary>
    public byte[]? Load(byte[] key, Guid cameraId)
    {
        try
        {
            var data = File.ReadAllBytes(PathOf(cameraId));
            var start = Header.Length + NonceSize + TagSize;
            if (data.Length < start || !data.AsSpan(0, Header.Length).SequenceEqual(Header))
                return null;
            var picture = new byte[data.Length - start];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(data.AsSpan(Header.Length, NonceSize), data.AsSpan(start), data.AsSpan(Header.Length + NonceSize, TagSize),
                picture, AssociatedData(cameraId));
            return picture;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    public void Delete(Guid cameraId) => File.Delete(PathOf(cameraId));

    private string PathOf(Guid cameraId) => Path.Combine(Folder, $"{cameraId:N}.bin");

    private static byte[] AssociatedData(Guid cameraId) => [.. Header, .. cameraId.ToByteArray()];

    private void CreateFolder()
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(Folder);
        else
            Directory.CreateDirectory(Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static FileStreamOptions PrivateFile()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
}
