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
    /// definition. The model contains exactly one and it has not been shown to
    /// lie on any region's boundary, so this is included on principle rather
    /// than on evidence, and is marked as such.
    ///
    /// CurtainWallDoorPanel - a door panel in a curtain wall is a door. The
    /// model contains one, in a wall typed "_Not Defined". Whether it opens onto
    /// the tiny space beside it is a geometric question answered elsewhere; if
    /// it does, a third of a square metre becomes a room, and that is a
    /// conclusion worth surfacing rather than one to be quietly prevented here.
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
    /// embeds runs of exterior rainscreen into exterior rainscreen. An earlier
    /// draft of this project intended to admit them, on the theory that a large
    /// interior room was entered through an embedded storefront; the model
    /// showed that those storefronts do not bound that room at all.
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
