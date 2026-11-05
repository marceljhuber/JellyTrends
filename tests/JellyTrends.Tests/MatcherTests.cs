using Jellyfin.Plugin.JellyTrends.Model;
using Jellyfin.Plugin.JellyTrends.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

public class MatcherTests
{
    private static Movie M(string name, int? year, string? imdb = null, string? original = null)
    {
        var m = new Movie { Id = Guid.NewGuid(), Name = name, ProductionYear = year, OriginalTitle = original };
        if (imdb is not null)
        {
            m.ProviderIds["Imdb"] = imdb;
        }

        return m;
    }

    private static LibraryMatcher.LibraryIndex Index(params BaseItem[] items)
    {
        var index = new LibraryMatcher.LibraryIndex();
        foreach (var i in items)
        {
            index.Add(i);
        }

        return index;
    }

    private static TrendingEntry E(string title, int? year, string? imdb = null) => new() { Title = title, Year = year, ImdbId = imdb };

    [Fact]
    public void Provider_id_beats_title()
    {
        var right = M("Dune: Part One", 2021, "tt1160419");
        var index = Index(M("Dune", 2021), right);
        Assert.Same(right, index.Find(E("Dune", 2021, "tt1160419"), strictYearMatch: true));
    }

    [Fact]
    public void Multi_value_provider_ids_are_split()
    {
        var item = M("X", 2000);
        item.ProviderIds["Imdb"] = "tt0000001|tt0000002";
        Assert.Same(item, Index(item).Find(E("other", null, "tt0000002"), true));
    }

    [Fact]
    public void Loose_match_accepts_a_nearby_year()
    {
        var talk = M("Talk to Me", 2023);
        Assert.Same(talk, Index(talk).Find(E("Talk To Me", 2022), strictYearMatch: false));
        Assert.Null(Index(talk).Find(E("Talk To Me", 2022), strictYearMatch: true));
    }

    [Fact]
    public void Loose_match_never_returns_a_far_away_namesake()
    {
        // Used to return the 1984 film for the 2021 chart entry.
        var old = M("Dune", 1984);
        Assert.Null(Index(old).Find(E("Dune", 2021), strictYearMatch: false));
    }

    [Fact]
    public void Loose_match_picks_the_closest_of_several()
    {
        var a = M("Halloween", 1978);
        var b = M("Halloween", 2018);
        Assert.Same(b, Index(a, b).Find(E("Halloween", 2019), false));
    }

    [Fact]
    public void Undated_library_item_is_still_accepted_loosely()
    {
        var undated = M("Obscure Film", null);
        Assert.Same(undated, Index(undated).Find(E("Obscure Film", 2010), false));
    }

    [Fact]
    public void Original_title_and_normalisation_match()
    {
        var amelie = M("Amélie", 2001, original: "Le Fabuleux Destin d'Amélie Poulain");
        var index = Index(amelie);
        Assert.Same(amelie, index.Find(E("Amelie", 2001), true));
        Assert.Same(amelie, index.Find(E("le fabuleux destin d amelie poulain", 2001), true));
        Assert.Equal("fast and furious", LibraryMatcher.NormalizeTitle("The Fast & Furious"));
    }

    [Fact]
    public void Finalize_dedupes_ranks_and_limits()
    {
        var list = TrendingService.Finalize([E("A", 1, "tt1"), E("A again", 1, "TT1"), E("B", 2), E("B", 2), E("C", 3), E("D", 4)], 3);
        Assert.Equal(["A", "B", "C"], list.Select(e => e.Title));
        Assert.Equal([1, 2, 3], list.Select(e => e.Rank));
    }

    [Theory]
    [InlineData("2019–2024", 2019)]
    [InlineData("2023-05-01", 2023)]
    [InlineData("n/a", null)]
    [InlineData(null, null)]
    public void Years_are_parsed_from_ranges_and_dates(string? text, int? year) => Assert.Equal(year, TrendingService.TryParseYear(text));
}
