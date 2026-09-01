using Findra.Pipe;
using Xunit;

public class MessageTests
{
    [Fact]
    public void QueryRoundTripsThroughAnEnvelope()
    {
        byte[] packed = Envelope.Pack(Envelope.KindQuery, new QueryRequest(7, "sunset ext:jpg", 400));

        Envelope e = Envelope.Unpack(packed);
        Assert.Equal(Envelope.KindQuery, e.Kind);

        QueryRequest got = e.Body<QueryRequest>();
        Assert.Equal(7, got.Gen);
        Assert.Equal("sunset ext:jpg", got.Raw);
        Assert.Equal(400, got.Max);
    }

    [Fact]
    public void ReplyCarriesTheGenerationItWasAskedWith()
    {
        var reply = new QueryReply(42, 'C', 1234, new[]
        {
            new NameRow(0xABC, "IMG_4471.HEIC", @"D:\Photos\2025\IMG_4471.HEIC", 0x20, 0.91f, 0),
        });

        QueryReply got = Envelope.Unpack(Envelope.Pack(Envelope.KindQueryReply, reply)).Body<QueryReply>();

        Assert.Equal(42, got.Gen);
        Assert.Equal('C', got.Volume);
        Assert.Single(got.Rows);
        Assert.Equal("IMG_4471.HEIC", got.Rows[0].Name);
        Assert.Equal(0xABCu, (uint)got.Rows[0].Frn);
    }

    [Fact]
    public void NonAsciiNamesSurviveTheWire()
    {
        var reply = new QueryReply(1, 'C', 0, new[]
        {
            new NameRow(1, "הסכם-שכירות 2026.docx", @"D:\מסמכים\הסכם-שכירות 2026.docx", 0x20, 1f, 0),
        });

        QueryReply got = Envelope.Unpack(Envelope.Pack(Envelope.KindQueryReply, reply)).Body<QueryReply>();

        Assert.Equal("הסכם-שכירות 2026.docx", got.Rows[0].Name);
        Assert.Equal(@"D:\מסמכים\הסכם-שכירות 2026.docx", got.Rows[0].Path);
    }

    [Fact]
    public void StatusRoundTrips()
    {
        var reply = new StatusReply(4242, new[] { new VolumeStatus('C', 1_532_238, 90_000_000, true) });

        StatusReply got = Envelope.Unpack(Envelope.Pack(Envelope.KindStatusReply, reply)).Body<StatusReply>();

        Assert.Equal(4242, got.ProcessId);
        Assert.Equal('C', got.Volumes[0].Letter);
        Assert.Equal(1_532_238, got.Volumes[0].Count);
        Assert.True(got.Volumes[0].Live);
    }

    [Fact]
    public void UnknownKindIsReadableWithoutThrowing()
    {
        // forward compatibility: an older client must skip a kind it does not know
        byte[] packed = Envelope.Pack("something-new", new QueryRequest(1, "x", 1));
        Envelope e = Envelope.Unpack(packed);
        Assert.Equal("something-new", e.Kind);
    }
}
