namespace SpatialAnalyzer.Core.Spatial;

/// <summary>
/// Decides what counts as a way into a space.
///
/// This is the judgement the brief leaves open, so it is written down in one
/// place, as data, where it can be read and argued with rather than inferred
/// from scattered conditionals. Every kind admitted or refused below is answered
/// for from the acceptance model, and where the model has nothing to say the
/// reason is stated as definitional instead of being dressed up as evidence.
///
/// The rule is deliberately not "is there a door in a wall that bounds this
/// space". That question gave a four square metre cupboard eight doors, because
/// a long wall lends its doors to every space it touches. What is passed here
/// has already been established to lie on this region's boundary.
/// </summary>
public sealed class EntranceRule
{
    private readonly HashSet<BoundaryFeatureKind> _admitting;

    public EntranceRule(IEnumerable<BoundaryFeatureKind> admittingKinds)
    {
        ArgumentNullException.ThrowIfNull(admittingKinds);
        _admitting = new HashSet<BoundaryFeatureKind>(admittingKinds);

        if (_admitting.Contains(BoundaryFeatureKind.Unknown))
        {
            throw new ArgumentException(
                "An unclassified boundary feature cannot be treated as an entrance. Whatever it is has to be identified first.",
                nameof(admittingKinds));
        }
    }

    /// <summary>
    /// What this project admits, and why.
    ///
    /// Door - the ordinary case, and the only one the acceptance level actually
    /// exercises. Nineteen of its thirty spaces are reached through one.
    ///
    /// Opening - a hole cut through a bounding element is a way through by
    /// definition. The model contains exactly one, and it is the sole way into a
    /// two and a third square metre space that would otherwise be rejected.
    ///
    /// CurtainWallDoorPanel - a door panel in a curtain wall is a door. Two
    /// spaces are reached through one, including a stair whose glazing carries a
    /// storefront door. The worry that recording these would turn a third of a
    /// square metre into a room came from reading which walls contain what;
    /// tested geometrically, that panel opens onto a four square metre lobby
    /// instead, and the sliver beside it has nothing on its boundary at all.
    ///
    /// What is refused, and why:
    ///
    /// Window - not a way in. Eleven sit on the boundaries of spaces that are
    /// plainly not rooms.
    ///
    /// SpecialtyEquipment - the important refusal. Twelve lift doors across two
    /// shafts and five trash chutes are set into bounding walls and read exactly
    /// like doors. Admitting them would turn two lift shafts and a refuse chute
    /// into rooms.
    ///
    /// CurtainWallPanel - glazing and solid infill. Forty-five glazed panels
    /// enclose a stair; twenty more make up two storefronts. None is a way
    /// through.
    ///
    /// EmbeddedWall - a wall embedded in a wall is still a wall. The model
    /// embeds runs of exterior rainscreen into exterior rainscreen, and glazed
    /// storefronts into a chase wall.
    ///
    /// This last one is the rule's known open case, and it is left open on
    /// purpose. A hundred and seven square metre retail unit is enclosed partly
    /// by two "Block 41 Storefront" curtain walls, which do lie on its boundary,
    /// and between them they have twenty solid panels and thirty glazed ones and
    /// no door. So the space is rejected: the model records no way into it. A
    /// person who knows the building will say a shopfront is obviously where you
    /// go in, and they are describing the building rather than the model.
    /// Admitting curtain walls would qualify that one space and change nothing
    /// else here - which is a reason to look again, not a justification, because
    /// a rule adjusted until one model's count comes out right has stopped being
    /// a rule.
    /// </summary>
    public static EntranceRule Default { get; } = new(new[]
    {
        BoundaryFeatureKind.Door,
        BoundaryFeatureKind.Opening,
        BoundaryFeatureKind.CurtainWallDoorPanel,
    });

    public IReadOnlyList<BoundaryFeatureKind> AdmittingKinds =>
        _admitting.OrderBy(k => k).ToList();

    public bool Admits(BoundaryFeatureKind kind) => _admitting.Contains(kind);

    /// <summary>
    /// The features on a boundary that this rule accepts as ways in, in the
    /// order they were supplied.
    /// </summary>
    public IReadOnlyList<BoundaryFeature> EntrancesAmong(IReadOnlyList<BoundaryFeature> boundaryFeatures)
    {
        ArgumentNullException.ThrowIfNull(boundaryFeatures);
        return boundaryFeatures.Where(f => Admits(f.Kind)).ToList();
    }
}
