using Findra;

using Xunit;

/// <summary>
/// Which chunks are allowed to claim a meaning.
///
/// <para>A passage embeds to a point in the model's space, and a passage too short to say one
/// thing rather than another lands near the middle of it - close to everything, which is the same
/// as close to nothing. Those chunks do not score badly against an unrelated query; they score
/// WELL against every query, and they beat the documents that actually answer it.</para>
///
/// <para>The fix is the one already used for the words inside pictures: the chunk is still stored
/// and still full-text indexed, so every word in it is findable. It simply stops claiming to have
/// a meaning. Nothing is lost and nothing is skipped - which is what separates this from a rule
/// that decides on somebody's behalf what is worth reading.</para>
/// </summary>
public class WorthEmbeddingTests
{
    private static string Words(int n, bool distinct = true)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(distinct ? $"word{i} " : "repeated ");
        return sb.ToString().Trim();
    }

    [Fact]
    public void AFullChunkOfProseIsWorthEmbedding()
    {
        // What a document is made of: Chunk() cuts at 1200 characters, so an ordinary chunk clears
        // every threshold with room to spare. If this ever fails the thresholds have swallowed the
        // normal case, which is the failure that matters.
        string prose = string.Join(" ", Enumerable.Range(0, 200).Select(i => $"sentence{i} about leases and payment terms"));
        Assert.True(DocText.WorthEmbedding(prose));
    }

    [Fact]
    public void ARealButTinySentenceIsNot()
    {
        // Not junk - a perfectly good English sentence, and that is the point. It is simply too
        // short to mean one thing rather than another, so it sits near the centre of the space and
        // out-scores real answers to queries it has nothing to do with.
        Assert.False(DocText.WorthEmbedding("Payment complete. Thank you for subscribing. Your account has been created."));
        Assert.False(DocText.WorthEmbedding("Search started. Your song's analysis has begun."));
    }

    [Fact]
    public void TheLastChunkOfADocumentIsUsuallyTheShortOne()
    {
        // Chunk() keeps anything over 40 characters, so nearly every document ends in a fragment.
        // Those fragments are the commonest instance of this defect, not an edge case.
        Assert.False(DocText.WorthEmbedding("Signed on the fourth of September."));
    }

    [Fact]
    public void LengthAloneIsNotEnoughAndRepetitionIsWhy()
    {
        // Long, and still says one word. A boilerplate footer repeated to fill a page clears any
        // character count while carrying no more meaning than the word it repeats.
        string repeated = Words(120, distinct: false);
        Assert.True(repeated.Length > 400, "the sample has to be long enough to pass the length test");
        Assert.False(DocText.WorthEmbedding(repeated));
    }

    [Fact]
    public void EveryThresholdHasToBeClearedAndNoneOfThemAlone()
    {
        // 60 words of one syllable can be under 400 characters, and 400 characters can be nine
        // long words. Each threshold catches a shape the others let through.
        Assert.False(DocText.WorthEmbedding(Words(70).Substring(0, 300)));          // words, not length
        Assert.False(DocText.WorthEmbedding(new string('x', 500) + " one two three")); // length, not words
    }

    [Fact]
    public void WhatSurvivesTagStrippingFromAComponentTemplateIsRefused()
    {
        // .html is a document extension because people save articles, and on a machine whose work
        // lives in repositories it is mostly component templates. StripTags leaves their button
        // labels and their interpolation markers behind, which is not prose in any language.
        string template = string.Join(" ", Enumerable.Range(0, 90).Select(i => $"{{{{ item{i}.name }}}} Save Cancel Delete"));
        Assert.True(template.Length > 400 && template.Split(' ').Length > 60, "the sample has to clear the size tests");
        Assert.False(DocText.WorthEmbedding(template));
    }

    [Fact]
    public void ProseThatMerelyMentionsBracesIsStillProse()
    {
        // The binding test is a density, not a search for a character. A document explaining
        // template syntax is a document, and refusing it would be the same defect one level along.
        string doc = string.Join(" ", Enumerable.Range(0, 120).Select(i =>
            i == 3 ? "the {{ value }} placeholder" : $"paragraph{i} explaining how the framework renders its output"));
        Assert.True(DocText.WorthEmbedding(doc));
    }

    [Fact]
    public void NothingIsEverSkippedByThisRule()
    {
        // The rule withholds a vector and nothing else. This is the whole difference between it
        // and a rule that decides what somebody is allowed to find: every word above is still in
        // the full-text index, so all of it is still searchable by its words.
        Assert.False(DocText.WorthEmbedding("too short"));
        Assert.True(DocText.Chunk("too short. " + new string('a', 100)).Count > 0);
    }
}
