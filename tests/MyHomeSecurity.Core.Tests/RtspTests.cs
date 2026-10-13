using System.Text;
using MyHomeSecurity.Core.Cameras;

namespace MyHomeSecurity.Core.Tests;

public sealed class RtspTests
{
    [Fact]
    public void RequestsCarryCSeqAndALengthForTheirBody()
    {
        var m = RtspMessage.Request("ANNOUNCE", "rtsp://cam/stream1", 7);
        m.Body = "v=0\r\n"u8.ToArray();
        m.Set("Content-Length", "999"); // stale: replaced from the body

        var text = Encoding.UTF8.GetString(m.ToBytes());

        Assert.StartsWith("ANNOUNCE rtsp://cam/stream1 RTSP/1.0\r\n", text);
        Assert.Contains("CSeq: 7\r\n", text);
        Assert.Contains("Content-Length: 5\r\n", text);
        Assert.DoesNotContain("999", text);
        Assert.EndsWith("\r\n\r\nv=0\r\n", text);
    }

    [Fact]
    public async Task ReaderSeparatesMessagesBodiesAndInterleavedData()
    {
        var bytes = new List<byte>();
        bytes.AddRange("RTSP/1.0 200 OK\r\nCSeq: 2\r\nContent-Type: application/sdp\r\nContent-Length: 4\r\n\r\nabcd"u8.ToArray());
        bytes.AddRange(new byte[] { (byte)'$', 0, 0, 3, 9, 8, 7 });
        bytes.AddRange("OPTIONS * RTSP/1.0\r\nCSeq: 3\r\n\r\n"u8.ToArray());
        var reader = new RtspStreamReader(new MemoryStream(bytes.ToArray()));

        var first = (await reader.ReadAsync(CancellationToken.None))!.Value.Message!;
        Assert.True(first.IsResponse);
        Assert.Equal(200, first.StatusCode);
        Assert.Equal("2", first.CSeq);
        Assert.Equal("abcd", Encoding.UTF8.GetString(first.Body));

        var data = (await reader.ReadAsync(CancellationToken.None))!.Value;
        Assert.Null(data.Message);
        Assert.Equal(new byte[] { (byte)'$', 0, 0, 3, 9, 8, 7 }, data.Data.ToArray());

        var third = (await reader.ReadAsync(CancellationToken.None))!.Value.Message!;
        Assert.Equal("OPTIONS", third.Method);
        Assert.Equal("*", third.Uri);

        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public void ParsesChallengeParametersWithQuotedCommas()
    {
        var p = RtspAuthenticator.ParseParameters("realm=\"My, Camera\", nonce=abc123, stale=FALSE");
        Assert.Equal("My, Camera", p["realm"]);
        Assert.Equal("abc123", p["nonce"]);
        Assert.Equal("FALSE", p["Stale"]);
    }

    [Fact]
    public void DigestWithoutQopMatchesRfc2617()
    {
        var auth = new RtspAuthenticator("admin", "secret");
        Assert.True(auth.Accept(["Basic realm=\"cam\"", "Digest realm=\"cam\", nonce=\"n1\""]));

        var header = auth.GetHeader("DESCRIBE", "rtsp://cam/stream1")!;

        var ha1 = RtspAuthenticator.Md5Hex("admin:cam:secret");
        var ha2 = RtspAuthenticator.Md5Hex("DESCRIBE:rtsp://cam/stream1");
        Assert.StartsWith("Digest ", header);
        Assert.Contains($"response=\"{RtspAuthenticator.Md5Hex($"{ha1}:n1:{ha2}")}\"", header);
        Assert.DoesNotContain("secret", header);
    }

    [Fact]
    public void DigestEchoesAlgorithmOnlyWhenChallenged()
    {
        // Tapo's challenge names no algorithm, and the camera refuses an answer that adds one.
        var tapo = new RtspAuthenticator("admin", "p@ss!");
        Assert.True(tapo.Accept(["Basic realm=\"TP-Link IP-Camera\"", "Digest realm=\"TP-Link IP-Camera\", nonce=\"n1\""]));
        Assert.DoesNotContain("algorithm", tapo.GetHeader("DESCRIBE", "rtsp://cam/stream1")!);

        var named = new RtspAuthenticator("admin", "secret");
        Assert.True(named.Accept(["Digest realm=\"cam\", nonce=\"n1\", algorithm=MD5"]));
        Assert.EndsWith(", algorithm=MD5", named.GetHeader("DESCRIBE", "rtsp://cam/stream1")!);
    }

    [Fact]
    public void DigestWithQopCountsNonces()
    {
        var auth = new RtspAuthenticator("admin", "secret");
        Assert.True(auth.Accept(["Digest realm=\"cam\", nonce=\"n1\", qop=\"auth,auth-int\", opaque=\"op\""]));

        var first = RtspAuthenticator.ParseParameters(auth.GetHeader("PLAY", "rtsp://cam/s")!["Digest ".Length..]);
        var second = RtspAuthenticator.ParseParameters(auth.GetHeader("PLAY", "rtsp://cam/s")!["Digest ".Length..]);

        Assert.Equal("00000001", first["nc"]);
        Assert.Equal("00000002", second["nc"]);
        Assert.Equal("op", first["opaque"]);
        var ha1 = RtspAuthenticator.Md5Hex("admin:cam:secret");
        var ha2 = RtspAuthenticator.Md5Hex("PLAY:rtsp://cam/s");
        Assert.Equal(RtspAuthenticator.Md5Hex($"{ha1}:n1:00000001:{first["cnonce"]}:auth:{ha2}"), first["response"]);
    }

    [Fact]
    public void FallsBackToBasic()
    {
        var auth = new RtspAuthenticator("admin", "secret");
        Assert.True(auth.Accept(["Digest realm=\"x\", nonce=\"y\", algorithm=SHA-256", "Basic realm=\"x\""]));
        Assert.Equal("Basic " + Convert.ToBase64String("admin:secret"u8.ToArray()), auth.GetHeader("DESCRIBE", "rtsp://x/"));
    }

    [Fact]
    public void UnknownChallengesAreRefused()
    {
        var auth = new RtspAuthenticator("admin", "secret");
        Assert.False(auth.Accept(["Bearer realm=\"x\""]));
        Assert.Null(auth.GetHeader("DESCRIBE", "rtsp://x/"));
    }
}
