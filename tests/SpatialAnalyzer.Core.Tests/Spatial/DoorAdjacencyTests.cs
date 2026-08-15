using SpatialAnalyzer.Core.Domain;
using SpatialAnalyzer.Core.Spatial;

namespace SpatialAnalyzer.Core.Tests.Spatial;

public class DoorAdjacencyTests
{
    private static ElementDescriptor ADoor() =>
        ElementDescriptor.Create(new RevitElementId(912), "Doors", "Single-Flush", "915 x 2134mm");

    [Fact]
    public void ADoorBetweenTwoRegionsNamesBoth()
    {
        DoorAdjacency adjacency = DoorAdjacency.Resolved(ADoor(), new RegionId(3), new RegionId(7));

        Assert.Equal(DoorConnection.BetweenTwoRegions, adjacency.Connection);
        Assert.True(adjacency.ConnectsTwoRegions);
        Assert.Equal(new[] { new RegionId(3), new RegionId(7) }, adjacency.Regions);
    }

    /// <summary>
    /// Revit's own FromRoom and ToRoom name the same room on both sides for six
    /// of the eighteen doors on the acceptance level, because those parameters
    /// describe placed rooms rather than granular spaces. Whatever resolves the
    /// sides, this outcome has to be expressible rather than treated as a bug.
    /// </summary>
    [Fact]
    public void ADoorWithTheSameRegionOnBothSidesSeparatesNothing()
    {
        DoorAdjacency adjacency = DoorAdjacency.Resolved(ADoor(), new RegionId(5), new RegionId(5));

        Assert.Equal(DoorConnection.WithinOneRegion, adjacency.Connection);
        Assert.False(adjacency.ConnectsTwoRegions);
        Assert.Equal(new[] { new RegionId(5) }, adjacency.Regions);
    }

    /// <summary>
    /// A door whose far side was not found is not thereby a door to the outside.
    /// Both are single-region results and would look identical as a count, which
    /// is why the description is derived from what was actually found on each
    /// side.
    /// </summary>
    [Fact]
    public void ADoorWithOnlyOneSideFoundDoesNotClaimToLeadOutside()
    {
        DoorAdjacency adjacency = DoorAdjacency.Resolved(ADoor(), new RegionId(2), null);

        Assert.Equal(DoorConnection.OneSideResolved, adjacency.Connection);
        Assert.False(adjacency.ConnectsTwoRegions);
        Assert.Equal(new[] { new RegionId(2) }, adjacency.Regions);
    }

    [Fact]
    public void WhichSideWasFoundDoesNotChangeTheDescription()
    {
        Assert.Equal(
            DoorAdjacency.Resolved(ADoor(), new RegionId(2), null).Connection,
            DoorAdjacency.Resolved(ADoor(), null, new RegionId(2)).Connection);
    }

    [Fact]
    public void ADoorWithNeitherSideFoundIsUnresolvedRatherThanEmpty()
    {
        DoorAdjacency adjacency = DoorAdjacency.Resolved(ADoor(), null, null);

        Assert.Equal(DoorConnection.Unresolved, adjacency.Connection);
        Assert.Empty(adjacency.Regions);
    }

    /// <summary>
    /// Reports and exports must read the same way for the same model, whichever
    /// order the geometry happened to be walked in.
    /// </summary>
    [Fact]
    public void TheRegionsAreListedInAStableOrderRegardlessOfWhichSideWasExaminedFirst()
    {
        DoorAdjacency oneWay = DoorAdjacency.Resolved(ADoor(), new RegionId(9), new RegionId(4));
        DoorAdjacency theOther = DoorAdjacency.Resolved(ADoor(), new RegionId(4), new RegionId(9));

        Assert.Equal(oneWay.Regions, theOther.Regions);
        Assert.Equal(new[] { new RegionId(4), new RegionId(9) }, oneWay.Regions);
    }

    /// <summary>
    /// Which side is which is still recorded, because it is what a caller needs
    /// to say where a selected element sits relative to the door.
    /// </summary>
    [Fact]
    public void TheSidesRemainDistinguishableEvenThoughTheListIsSorted()
    {
        DoorAdjacency adjacency = DoorAdjacency.Resolved(ADoor(), new RegionId(9), new RegionId(4));

        Assert.Equal(new RegionId(9), adjacency.SideA);
        Assert.Equal(new RegionId(4), adjacency.SideB);
    }

    [Fact]
    public void TheDoorItselfIsAlwaysReported()
    {
        ElementDescriptor door = ADoor();

        Assert.Same(door, DoorAdjacency.Resolved(door, null, null).Door);
    }
}
